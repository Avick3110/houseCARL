using HousecarlMcp;
using Xunit;
using W = HousecarlMcpTests.NifSourceLaneWorld;

namespace HousecarlMcpTests;

/// <summary>#412: nif_inspect's npc= resolves an NPC FormID to its FaceGen head mesh and reads it as one more member of
/// the batch, in the same call as an explicit path.</summary>
[Trait("tier", "integration")]
public sealed class NifSourceLaneNpcTests : IClassFixture<NifSourceLaneWorld>
{
    readonly NifSourceLaneWorld _w;
    public NifSourceLaneNpcTests(NifSourceLaneWorld w) => _w = w;

    // Probe: "an NPC FormID alone derives its facegeom .nif and reads it".
    [Fact]
    public void AnNpcFormIdAloneDerivesItsFacegeomNifAndReadsIt()
    {
        var text = NifTools.NifInspect(_w.Svc, null, npc: new[] { W.NpcFormId });

        Assert.Contains(W.FaceRel, text);
        Assert.Contains("read from:", text);
    }

    // Probe: "mesh_paths and npc compose in ONE call".
    [Fact]
    public void MeshPathsAndNpcComposeInOneCall()
        => Assert.Contains("(2 meshes)", NifTools.NifInspect(_w.Svc, new[] { W.FaceRel }, npc: new[] { W.NpcFormId }));

    // Probe: "neither selector is a named refusal that names both".
    [Fact]
    public void NeitherSelectorIsANamedRefusalThatNamesBoth()
    {
        var text = NifTools.NifInspect(_w.Svc, null, null);

        Assert.StartsWith("error:", text);
        Assert.Contains("mesh_paths", text);
        Assert.Contains("npc", text);
    }

    // Probe: "a malformed npc FormID is refused by name".
    [Fact]
    public void AMalformedNpcFormIdIsRefusedByName()
    {
        var text = NifTools.NifInspect(_w.Svc, null, npc: new[] { "not-a-formid" });

        Assert.StartsWith("error:", text);
        Assert.Contains("not-a-formid", text);
    }
}
