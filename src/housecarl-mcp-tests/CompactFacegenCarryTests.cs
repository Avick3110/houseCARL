using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Compacting an NPC plugin carries its FormID-keyed FaceGen files (head mesh and face tint) to the NPC's new
/// FormID, so the compacted mod does not dark-face: to a fresh mod folder, in place, across overlapping old and new
/// ids, and with nothing to carry. Driven through <c>LoadOrderService.CompactPlugin</c>.</summary>
[Trait("tier", "integration")]
public sealed class CompactFacegenCarryTests
{
    static readonly byte[] Mesh = { 0x4E, 0x49, 0x46, 0x01, 0x02, 0x03 };
    static readonly byte[] Tint = { 0xDD, 0x5B, 0xEE, 0xFF, 0x10 };

    static CompactCarryInstance OneFacedNpc(string stem, uint oldId, string edid)
    {
        var inst = new CompactCarryInstance(stem, m => m.Npcs.Add(new Npc(new FormKey(new ModKey(stem, ModType.Plugin), oldId), SkyrimRelease.SkyrimSE) { EditorID = edid }));
        var old = new FormKey(inst.Key, oldId);
        inst.Loose(FaceGenPath.For(old, FaceGenSlot.Mesh), Mesh);
        inst.Loose(FaceGenPath.For(old, FaceGenSlot.Tint), Tint);
        return inst;
    }

    static FormKey NpcKey(string pluginPath, string edid)
    {
        using var pp = SkyrimMod.CreateFromBinaryOverlay(pluginPath, SkyrimRelease.SkyrimSE);
        return pp.Npcs.Single(n => n.EditorID == edid).FormKey;
    }

    // NEW-FILE: facegen (mesh + tint) carried byte-exact to the NEW FormID, in the light window, under the fresh mod folder;
    // the report says 2 files / 1 NPC carried of 1, no failures
    [Fact]
    public void ANewFileCompactCarriesBothFacegenFilesToTheNewId()
    {
        using var inst = OneFacedNpc("FaceMod", 0xA10, "HcFaceNpc");
        var o = inst.Compact();
        Assert.True(o.Success, o.Error);
        var nk = NpcKey(o.OutputPath, "HcFaceNpc");
        Assert.InRange(nk.ID, RemapEngine.EslFloor, RemapEngine.EslCeiling);
        Assert.NotEqual(0xA10u, nk.ID);
        var root = Path.GetDirectoryName(o.OutputPath)!;
        Assert.NotEqual(inst.ModDir, root);
        Assert.True(CompactCarryInstance.Holds(Path.Combine(root, FaceGenPath.For(nk, FaceGenSlot.Mesh)), Mesh));
        Assert.True(CompactCarryInstance.Holds(Path.Combine(root, FaceGenPath.For(nk, FaceGenSlot.Tint)), Tint));
        Assert.Equal(1, o.AssetRename?.NpcCount);
        Assert.Equal(1, o.AssetRename?.FacegenNpcsCarried);
        Assert.Equal(2, o.AssetRename?.FacegenFilesCarried);
        Assert.Empty(o.AssetRename!.Failures);
    }

    // NEW-FILE: the OLD-FormID facegen is left untouched (non-destructive)
    [Fact]
    public void ANewFileCompactLeavesTheOldFacegenUntouched()
    {
        using var inst = OneFacedNpc("FaceMod", 0xA10, "HcFaceNpc");
        Assert.True(inst.Compact().Success);
        Assert.True(CompactCarryInstance.Holds(
            Path.Combine(inst.ModDir, FaceGenPath.For(new FormKey(inst.Key, 0xA10), FaceGenSlot.Mesh)), Mesh));
    }

    // IN-PLACE: facegen carried into the target's OWN folder at the new FormID; the old file stays as a harmless orphan
    [Fact]
    public void AnInPlaceCompactCarriesFacegenIntoTheTargetsFolderAndLeavesTheOldOrphan()
    {
        using var inst = OneFacedNpc("FaceIp", 0xB20, "HcIpNpc");
        var o = inst.Compact(inPlace: true);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        var nk = NpcKey(o.OutputPath, "HcIpNpc");
        Assert.NotEqual(0xB20u, nk.ID);
        Assert.True(CompactCarryInstance.Holds(Path.Combine(inst.ModDir, FaceGenPath.For(nk, FaceGenSlot.Mesh)), Mesh));
        Assert.Equal(2, o.AssetRename?.FacegenFilesCarried);
        Assert.Equal(1, o.AssetRename?.FacegenNpcsCarried);
        Assert.True(File.Exists(Path.Combine(inst.ModDir, FaceGenPath.For(new FormKey(inst.Key, 0xB20), FaceGenSlot.Mesh))));
    }

    // IN-PLACE ALIASING: P (old 0x900, enumerated first) renumbers to 0x800, which is Q's OLD id; each keeps its OWN face
    [Fact]
    public void OverlappingOldAndNewIdsKeepEachNpcsOwnFace()
    {
        byte[] meshP = { 0x50, 0x01 }, tintP = { 0x50, 0x02 }, meshQ = { 0x51, 0x01 }, tintQ = { 0x51, 0x02 };
        var key = new ModKey("FaceAlias", ModType.Plugin);
        var pOld = new FormKey(key, 0x900);
        var qOld = new FormKey(key, 0x800);
        using var inst = new CompactCarryInstance("FaceAlias", m =>
        {
            m.Npcs.Add(new Npc(pOld, SkyrimRelease.SkyrimSE) { EditorID = "NpcP" });
            m.Npcs.Add(new Npc(qOld, SkyrimRelease.SkyrimSE) { EditorID = "NpcQ" });
        });
        inst.Loose(FaceGenPath.For(pOld, FaceGenSlot.Mesh), meshP);
        inst.Loose(FaceGenPath.For(pOld, FaceGenSlot.Tint), tintP);
        inst.Loose(FaceGenPath.For(qOld, FaceGenSlot.Mesh), meshQ);
        inst.Loose(FaceGenPath.For(qOld, FaceGenSlot.Tint), tintQ);

        var o = inst.Compact(inPlace: true);
        Assert.True(o.Success, o.Error);
        var pNew = NpcKey(o.OutputPath, "NpcP");
        var qNew = NpcKey(o.OutputPath, "NpcQ");
        Assert.Equal(qOld, pNew);   // the overlap the test exists for: P's new id is Q's old one
        var root = Path.GetDirectoryName(o.OutputPath)!;
        Assert.True(CompactCarryInstance.Holds(Path.Combine(root, FaceGenPath.For(pNew, FaceGenSlot.Mesh)), meshP));
        Assert.True(CompactCarryInstance.Holds(Path.Combine(root, FaceGenPath.For(qNew, FaceGenSlot.Mesh)), meshQ));
    }

    // NO-FACEGEN: an NPC with no facegen carries nothing and is NOT a failure
    [Fact]
    public void AnNpcWithNoFacegenCarriesNothingAndIsNotAFailure()
    {
        using var inst = new CompactCarryInstance("NoFg",
            m => m.Npcs.Add(new Npc(new FormKey(new ModKey("NoFg", ModType.Plugin), 0xC30), SkyrimRelease.SkyrimSE) { EditorID = "HcNoFgNpc" }));
        var o = inst.Compact();
        Assert.True(o.Success, o.Error);
        Assert.Equal(1, o.AssetRename?.NpcCount);
        Assert.Equal(0, o.AssetRename?.FacegenFilesCarried);
        Assert.Equal(0, o.AssetRename?.FacegenNpcsCarried);
        Assert.Empty(o.AssetRename!.Failures);
    }
}
