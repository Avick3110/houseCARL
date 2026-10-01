using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>Concurrent writes through the service never share an output folder or cross-commit, two extends of one
/// patch both land, a refused write leaves no folder behind, a dotted patch name keeps every segment, and a failed
/// rider removes only an empty fresh folder. Migrated from the write-mutex-guard probe.</summary>
[Trait("tier", "integration")]
public sealed class WriteMutexTests : IDisposable
{
    readonly ScratchMo2 _mo2 = new("hc-write-mutex-");
    readonly LoadOrderService _svc;
    readonly FormKey _weap;
    readonly string _fid;

    public WriteMutexTests()
    {
        var key = new ModKey("HcWmxMaster", ModType.Master);
        var m = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        var w = m.Weapons.AddNew(); w.EditorID = "HcWmxWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
        _weap = w.FormKey;
        m.BeginWrite.ToPath(_mo2.InMod("MasterMod", key)).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
        _mo2.Profile(key.FileName + "\r\n", "*" + key.FileName + "\r\n", "+MasterMod\r\n");
        _fid = ScratchMo2.Fid(_weap);
        _svc = _mo2.Open();
    }

    public void Dispose() { _svc.Dispose(); _mo2.Delete(); }

    BulkOp Op(string field, string value) => new() { Formid = _fid, FieldPath = field, Verb = "Set", Value = value };

    (ushort Damage, float Weight) Written(string path)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var s = ov.Weapons.Single(x => x.FormKey == _weap).BasicStats!;
        return (s.Damage, s.Weight);
    }

    // CONCURRENT: every concurrent write succeeded; every call got its OWN output path; every written file carries ITS OWN edit
    [Fact]
    public void SimultaneousDefaultNameWritesGetTheirOwnFileWithTheirOwnEdit()
    {
        var all = new List<(int Dmg, WritePatchBuilder.PatchOutcome O)>();
        for (int i = 0; i < 6; i++)
        {
            int a = 100 + i * 2, b = 101 + i * 2;
            using var barrier = new Barrier(2);
            var tA = Task.Run(() => { barrier.SignalAndWait(); return _svc.ApplyEdits(new[] { Op("BasicStats.Damage", a.ToString()) }, null, null); });
            var tB = Task.Run(() => { barrier.SignalAndWait(); return _svc.ApplyEdits(new[] { Op("BasicStats.Damage", b.ToString()) }, null, null); });
            all.Add((a, tA.GetAwaiter().GetResult()));
            all.Add((b, tB.GetAwaiter().GetResult()));
        }
        Assert.All(all, x => Assert.True(x.O.Success, x.O.Error));
        Assert.Equal(all.Count, all.Select(x => x.O.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, x => Assert.Equal((ushort)x.Dmg, Written(x.O.OutputPath).Damage));
    }

    // EXTEND: both extends succeeded; BOTH edits survive in the final file (no lost update)
    [Fact]
    public void TwoSimultaneousExtendsOfOnePatchBothLand()
    {
        var seed = _svc.ApplyEdits(new[] { Op("BasicStats.Damage", "50") }, "HcWmxExtend", null);
        Assert.True(seed.Success, seed.Error);
        using var barrier = new Barrier(2);
        var tA = Task.Run(() => { barrier.SignalAndWait(); return _svc.ApplyEdits(new[] { Op("BasicStats.Damage", "60") }, null, "HcWmxExtend"); });
        var tB = Task.Run(() => { barrier.SignalAndWait(); return _svc.ApplyEdits(new[] { Op("BasicStats.Weight", "7") }, null, "HcWmxExtend"); });
        var (oA, oB) = (tA.GetAwaiter().GetResult(), tB.GetAwaiter().GetResult());
        Assert.True(oA.Success, oA.Error);
        Assert.True(oB.Success, oB.Error);

        var w = Written(seed.OutputPath);
        Assert.Equal((ushort)60, w.Damage);
        Assert.Equal(7f, w.Weight);
    }

    // ORPHAN: both bad writes refused; refusal says NO patch written; no orphan folder remains;
    // a later good write gets the BASE stem, not an accreted _NNN
    [Fact]
    public void ARefusedWriteLeavesNoFolderAndALaterWriteGetsTheBaseStem()
    {
        var bad = Op("NoSuchFieldAnywhere", "1");
        var r1 = _svc.ApplyEdits(new[] { bad }, "HcWmxOrphan", null);
        var r2 = _svc.ApplyEdits(new[] { bad }, "HcWmxOrphan", null);
        Assert.False(r1.Success);
        Assert.False(r2.Success);
        Assert.Contains("NO patch written", r1.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateDirectories(_mo2.ModsDir, "houseCARL - HcWmxOrphan*"));

        var good = _svc.ApplyEdits(new[] { Op("BasicStats.Damage", "75") }, "HcWmxOrphan", null);
        Assert.True(good.Success, good.Error);
        Assert.EndsWith(Path.Combine("houseCARL - HcWmxOrphan", "HcWmxOrphan.esp"), good.OutputPath, StringComparison.OrdinalIgnoreCase);
    }

    // DOTTED: the dotted stem survives — folder + plugin keep every segment;
    // into='My.Cool.Patch.esp' resolves the SAME folder — extension stripped, inner dots kept
    [Fact]
    public void ADottedPatchNameKeepsEverySegmentAndIntoStripsOnlyTheExtension()
    {
        var dot = _svc.ApplyEdits(new[] { Op("BasicStats.Damage", "80") }, "My.Cool.Patch", null);
        Assert.True(dot.Success, dot.Error);
        Assert.EndsWith(Path.Combine("houseCARL - My.Cool.Patch", "My.Cool.Patch.esp"), dot.OutputPath, StringComparison.OrdinalIgnoreCase);

        var ext = _svc.ApplyEdits(new[] { Op("BasicStats.Weight", "9") }, null, "My.Cool.Patch.esp");
        Assert.True(ext.Success, ext.Error);
        Assert.Equal(dot.OutputPath, ext.OutputPath, ignoreCase: true);
    }

    // RIDER RESIDUE: a fresh rider folder is created; a genuinely-empty fresh rider folder is DELETED on failure
    [Fact]
    public void AnEmptyFreshRiderFolderIsDeleted()
    {
        var rf = _svc.OutputArea.ResolvePatchModFolder("HcRiderEmpty", null, "HcRiderDefault", BsaTools.RepackNaming);
        Assert.True(rf.CreatedFresh);
        Assert.True(Directory.Exists(rf.ModFolder));

        Assert.Null(OutputLocations.RemoveOrNameRiderResidue(rf));
        Assert.False(Directory.Exists(rf.ModFolder));
    }

    // RIDER RESIDUE: a fresh rider folder holding REAL output is KEPT and its path named
    [Fact]
    public void AFreshRiderFolderHoldingOutputIsKeptAndNamed()
    {
        var rf = _svc.OutputArea.ResolvePatchModFolder("HcRiderFull", null, "HcRiderDefault", BsaTools.RepackNaming);
        File.WriteAllText(Path.Combine(rf.OutputDir, "Output.bsa"), "data");

        Assert.Equal(rf.ModFolder, OutputLocations.RemoveOrNameRiderResidue(rf));
        Assert.True(Directory.Exists(rf.ModFolder));
    }

    // RIDER RESIDUE: an into= reuse is not flagged fresh; an into= reused folder is NEVER deleted or named
    [Fact]
    public void AnIntoReusedRiderFolderIsNeverTouched()
    {
        _svc.OutputArea.ResolvePatchModFolder("HcRiderInto", null, "HcRiderDefault", BsaTools.RepackNaming);
        var reuse = _svc.OutputArea.ResolvePatchModFolder(null, "HcRiderInto", "HcRiderDefault", BsaTools.RepackNaming);
        Assert.False(reuse.CreatedFresh);

        Assert.Null(OutputLocations.RemoveOrNameRiderResidue(reuse));
        Assert.True(Directory.Exists(reuse.ModFolder));
    }
}
