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
        var text = NifTools.NifInspect(_w.Svc, null, formids: new[] { W.NpcFormId });

        Assert.Contains(W.FaceRel, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("read from:", text);
    }

    // Probe: "mesh_paths and npc compose in ONE call".
    [Fact]
    public void MeshPathsAndNpcComposeInOneCall()
        => Assert.Contains("(2 meshes)", NifTools.NifInspect(_w.Svc, new[] { W.FaceRel }, formids: new[] { W.NpcFormId }));

    // Q1 (a): formids= names both halves of the pair, as on asset_status; nif_inspect reads the head mesh and counts the tint.
    [Fact]
    public void FormIdsReadsTheHeadMeshAndCountsTheTintInANote()
    {
        var text = NifTools.NifInspect(_w.Svc, formids: new[] { W.NpcFormId });

        Assert.Contains("(1 mesh)", text);
        Assert.DoesNotContain(".dds\n", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("note: 1 FaceGen tint(s) the formids= NPCs name are textures, not meshes, and were not read; " +
                        ToolNames.AssetStatus + " formids= reads them.", text);
    }

    // Probe: "no selector is a named refusal that names every one".
    [Fact]
    public void NoSelectorIsANamedRefusalThatNamesEveryOne()
    {
        var text = NifTools.NifInspect(_w.Svc, null, null);

        Assert.StartsWith("error:", text);
        foreach (var knob in new[] { "paths=", "under=", "provider=", "formids=" }) Assert.Contains(knob, text);
    }

    // Q1 (a): a malformed FormID is its own error row, as on asset_status formids=, and the other FormIDs still read.
    [Fact]
    public void AMalformedFormIdIsOneErrorRowAndTheRestRead()
    {
        var text = NifTools.NifInspect(_w.Svc, null, formids: new[] { "not-a-formid", W.NpcFormId });

        Assert.DoesNotContain("error:", text.Split('\n')[0]);
        Assert.Contains("\nnot-a-formid\n  not a FormID:", text);
        Assert.Contains(W.FaceRel, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[accounting] total=2 rendered=2", text);
    }
}
