using System.Linq;
using System.Text;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>NifService.Inspect, the byte reader behind housecarl_nif_inspect, reads every header, block, node and
/// shape value of an authored SE mesh exactly, and a bad file comes back as a named error.</summary>
[Trait("tier", "unit")]
public sealed class NifInspectDecodeTests
{
    static readonly NifInspectOutcome Outcome = NifService.Inspect(NifInspectFixtures.BuildSyntheticSe());
    static NifInspect Nif => Outcome.Inspect ?? throw new Xunit.Sdk.XunitException("the authored mesh did not parse: " + Outcome.Error);
    static NifShape Shape => Nif.Shapes.Single(s => s.Name == "GuardShape");

    // probe: "the authored mesh parses clean"
    [Fact]
    public void TheAuthoredMeshParsesClean()
    {
        Assert.Null(Outcome.Error);
        Assert.NotNull(Outcome.Inspect);
    }

    // probe: "header identity: SE stream, user 12 / stream 100"
    [Fact]
    public void TheHeaderIsAnSeStreamAtUser12Stream100()
    {
        Assert.True(Nif.IsSkyrimSE);
        Assert.Equal(12u, (uint)Nif.UserVersion);
        Assert.Equal(100u, (uint)Nif.StreamVersion);
    }

    // probe: "version string is 20.2.0.7"
    [Fact]
    public void TheVersionStringIs20207()
        => Assert.Contains("20.2.0.7", Nif.VersionString);

    // probe: "no unknown blocks in an authored SE mesh"
    [Fact]
    public void AnAuthoredSeMeshHasNoUnknownBlocks()
    {
        Assert.False(Nif.HasUnknownBlocks);
        Assert.Empty(Nif.UnknownBlockTypes);
    }

    // probe: "block count 8"
    [Fact]
    public void TheBlockCountIsEight()
        => Assert.Equal(8, Nif.BlockCount);

    // probe: "block census matches what was authored"
    [Theory]
    [InlineData("NiNode", 3)]
    [InlineData("BSTriShape", 1)]
    [InlineData("BSDismemberSkinInstance", 1)]
    [InlineData("NiAlphaProperty", 1)]
    [InlineData("BSLightingShaderProperty", 1)]
    [InlineData("BSShaderTextureSet", 1)]
    public void TheBlockCensusMatchesWhatWasAuthored(string type, int count)
        => Assert.Contains(Nif.BlockTypes, t => t.Type == type && t.Count == count);

    // probe: "node tree walks root→A→B at depths 0/1/2"
    [Fact]
    public void TheNodeTreeWalksRootThenAThenBAtDepthsZeroOneTwo()
    {
        Assert.Equal(3, Nif.Nodes.Count);
        Assert.Equal(("GuardRoot", 0), (Nif.Nodes[0].Name, Nif.Nodes[0].Depth));
        Assert.Contains(Nif.Nodes, n => n is { Name: "GuardChildA", Depth: 1 });
        Assert.Contains(Nif.Nodes, n => n is { Name: "GuardChildB", Depth: 2 });
    }

    // probe: "a node's NiAVObject flags read exactly (0x40000E)"
    [Fact]
    public void ANodesNiAvObjectFlagsReadExactly()
        => Assert.Equal(0x40000Eu, Nif.Nodes.Single(n => n.Name == "GuardChildA").Flags);

    // probe: "the header string table carries the authored names"
    [Theory]
    [InlineData("GuardRoot")]
    [InlineData("GuardShape")]
    public void TheHeaderStringTableCarriesTheAuthoredNames(string name)
        => Assert.Contains(name, Nif.HeaderStrings);

    // probe: "the authored shape is found" + "shape NiAVObject flags 0x400000E"
    [Fact]
    public void TheShapesNiAvObjectFlagsReadExactly()
        => Assert.Equal(0x400000Eu, Shape.Flags);

    // probe: "shape scale 1.25"
    [Fact]
    public void TheShapesScaleReadsExactly()
        => Assert.Equal(1.25f, Shape.Scale, 6);

    // probe: "BSDismember partitions decode to SBP_* names + flags"
    [Fact]
    public void DismemberPartitionsDecodeToSbpNamesAndFlags()
    {
        Assert.Equal(2, Shape.Partitions.Count);
        Assert.Equal(new NifPartition(30, "SBP_30_HEAD", 257), Shape.Partitions[0]);
        Assert.Equal(new NifPartition(31, "SBP_31_HAIR", 257), Shape.Partitions[1]);
    }

    // probe: "alpha property decodes (0x12ED, blend+test, thr 128)"
    [Fact]
    public void TheAlphaPropertyDecodesFlagsBlendTestAndThreshold()
    {
        var a = Shape.Alpha;
        Assert.NotNull(a);
        Assert.Equal(0x12ED, (int)a!.Flags);
        Assert.True(a.Blend);
        Assert.True(a.Test);
        Assert.Equal(128, (int)a.Threshold);
    }

    // probe: "shape flag default resolved from nif.xml (BSTriShape → 0x8000E)"
    [Fact]
    public void TheShapesFlagDefaultResolvesFromNifXml()
    {
        Assert.Equal("BSTriShape", Shape.BlockType);
        Assert.Equal(0x8000Eu, Shape.FlagsDefault);
        Assert.Equal("BSTriShape", Shape.FlagsDefaultType);
    }

    // probe: "node flag default resolved from nif.xml (NiNode → 0xE)"
    [Fact]
    public void ANodesFlagDefaultResolvesFromNifXml()
    {
        var a = Nif.Nodes.Single(n => n.Name == "GuardChildA");
        Assert.Equal("NiNode", a.BlockType);
        Assert.Equal(0xEu, a.FlagsDefault);
        Assert.Equal("NiNode", a.FlagsDefaultType);
    }

    // probe: "nif.xml SSE flag-default transcription intact (distinctive entries pinned against drift)"
    [Theory]
    [InlineData("BSTriShape", 0x8000Eu)]
    [InlineData("NiNode", 0xEu)]
    [InlineData("BSLeafAnimNode", 0x808000Eu)]
    [InlineData("BSMeshLODTriShape", 0x100Eu)]
    [InlineData("BSOrderedNode", 0x8200Eu)]
    [InlineData("BSLODTriShape", 0x800000Eu)]
    [InlineData("BSTreeNode", 0x8080Eu)]
    [InlineData("BSBlastNode", 0x8000Fu)]
    [InlineData("BSMultiBoundNode", 0xEu)]
    public void TheNifXmlSseFlagDefaultTranscriptionIsIntact(string type, uint flags)
        => Assert.Equal(flags, NifService.AvFlagsSseDefaults[type]);

    // probe: "empty bytes → named error"
    [Fact]
    public void EmptyBytesReturnANamedError()
    {
        var o = NifService.Inspect(Array.Empty<byte>());
        Assert.Null(o.Inspect);
        Assert.False(string.IsNullOrWhiteSpace(o.Error));
    }

    // probe: "non-NIF garbage → named error, not a throw"
    [Fact]
    public void NonNifGarbageReturnsANamedErrorNotAThrow()
    {
        var o = NifService.Inspect(Encoding.ASCII.GetBytes("this is plainly not a NIF file, just ASCII text padding padding padding."));
        Assert.Null(o.Inspect);
        Assert.False(string.IsNullOrWhiteSpace(o.Error));
    }
}
