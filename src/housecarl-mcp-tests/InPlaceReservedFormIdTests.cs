using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The in-place lanes refuse a plugin that defines records of its own below 0x800 before writing (#1049);
/// the default lane still writes an override of such a record, and a plugin whose only sub-0x800 records are
/// overrides of its master is not refused.</summary>
[Trait("tier", "integration")]
public sealed class InPlaceReservedFormIdTests : IDisposable
{
    const string MasterName = "HcResMaster.esm";
    const string OwnName = "HcResOwn.esp";
    const string OvrName = "HcResOvr.esp";
    const string Below = "below 0x800";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-inplace-reserved-" + Guid.NewGuid().ToString("N"));
    readonly string _master, _own, _ovr;
    readonly FormKey _masterLow, _ownLow;

    public InPlaceReservedFormIdTests()
    {
        Directory.CreateDirectory(_dir);
        _master = Path.Combine(_dir, MasterName);
        _own = Path.Combine(_dir, OwnName);
        _ovr = Path.Combine(_dir, OvrName);

        var mKey = new ModKey("HcResMaster", ModType.Master);
        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        _masterLow = new FormKey(mKey, 0x123);
        m.Weapons.Add(new Weapon(_masterLow, SkyrimRelease.SkyrimSE) { EditorID = "HcRes_MasterLow", BasicStats = new WeaponBasicStats { Damage = 10 } });
        m.BeginWrite.ToPath(_master).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoCheckIfLowerRangeDisallowed().Write();
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(_master, SkyrimRelease.SkyrimSE);
        var masterWeap = mOv.Weapons.Single();

        // Defines a weapon of its own at 0x456, the shape of a vanilla or Creation Club master.
        var oKey = new ModKey("HcResOwn", ModType.Plugin);
        var o = new SkyrimMod(oKey, SkyrimRelease.SkyrimSE);
        _ownLow = new FormKey(oKey, 0x456);
        o.Weapons.Add(new Weapon(_ownLow, SkyrimRelease.SkyrimSE) { EditorID = "HcRes_OwnLow", BasicStats = new WeaponBasicStats { Damage = 7 } });
        o.Weapons.GetOrAddAsOverride(masterWeap).BasicStats!.Damage = 20;
        o.BeginWrite.ToPath(_own).WithLoadOrder(new ISkyrimModGetter[] { mOv }).NoCheckIfLowerRangeDisallowed().NoNextFormIDProcessing().Write();

        // Its only sub-0x800 record is the override of the master's 0x123; its own record sits at 0x900.
        var vKey = new ModKey("HcResOvr", ModType.Plugin);
        var v = new SkyrimMod(vKey, SkyrimRelease.SkyrimSE);
        v.Weapons.GetOrAddAsOverride(masterWeap).BasicStats!.Damage = 30;
        v.Keywords.Add(new Keyword(new FormKey(vKey, 0x900), SkyrimRelease.SkyrimSE) { EditorID = "HcRes_OvrKw" });
        v.BeginWrite.ToPath(_ovr).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    LoadOrderService Service(params string[] order)
    {
        var svc = LoadOrderService.ForGuard(LoadOrderResolver.Build(order), new UserConfigStore(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json")));
        svc.Stats();
        return svc;
    }

    static string Id(FormKey fk, string plugin) => $"{fk.ID:X6}:{plugin}";

    static BulkOp[] SetDamage(string formid) =>
        new[] { new BulkOp { Formid = formid, FieldPath = "BasicStats.Damage", Verb = "Set", Value = "55" } };

    static int Damage(string path, FormKey fk)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return (int?)ov.Weapons.FirstOrDefault(x => x.FormKey == fk)?.BasicStats?.Damage ?? -1;
    }

    [Fact]
    public void AnInPlaceApplyToAPluginDefiningASub0x800RecordIsRefusedWithTheFileByteIdentical()
    {
        var before = File.ReadAllBytes(_own);
        using var svc = Service(_master, _own);
        var o = svc.ApplyEdits(SetDamage(Id(_ownLow, OwnName)), null, null, target: OwnName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains(Below, o.Error, StringComparison.Ordinal);
        Assert.Contains(LocalizedTargetUnsupportedException.RemedyDefaultLane, o.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(_own));
    }

    [Fact]
    public void AnInPlaceCreateIntoThatPluginIsRefused()
    {
        var before = File.ReadAllBytes(_own);
        using var svc = Service(_master, _own);
        var o = svc.InPlaceGuardCreate("Keyword", "HcRes_NewKw", Array.Empty<BulkOp>(), null, null, target: OwnName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains(Below, o.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(_own));
    }

    [Fact]
    public void AnInPlaceRemoveFromThatPluginIsRefused()
    {
        var before = File.ReadAllBytes(_own);
        using var svc = Service(_master, _own);
        var o = svc.RemoveRecords(new[] { Id(_masterLow, MasterName) }, null, target: OwnName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains(Below, o.Error, StringComparison.Ordinal);
        Assert.Contains(LocalizedTargetUnsupportedException.RemoveNoEquivalent, o.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(_own));
    }

    [Fact]
    public void AnInPlaceForwardIntoThatPluginIsRefused()
    {
        var before = File.ReadAllBytes(_own);
        using var svc = Service(_master, _own);
        var o = svc.ForwardRecords(new[] { Id(_masterLow, MasterName) }, MasterName, null, null, target: OwnName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains(Below, o.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(_own));
    }

    [Fact]
    public void TheDefaultLaneStillWritesAnOverrideOfThatPluginsSub0x800Record()
    {
        var outPath = Path.Combine(_dir, "out", "HcResPatch.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        using var r = LoadOrderResolver.Build(new[] { _master, _own });
        var edit = new WritePatchBuilder.PatchEdit { Target = _ownLow, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "55" };
        var o = WritePatchBuilder.Apply(r, TestCorpus.Rulebook, new[] { edit }, outPath, extend: false);
        Assert.True(o.Success, o.Error);
        Assert.Equal(55, Damage(outPath, _ownLow));
    }

    [Fact]
    public void APluginWhoseOnlySub0x800RecordsAreOverridesOfItsMasterIsNotRefused()
    {
        using var svc = Service(_master, _ovr);
        var o = svc.ApplyEdits(SetDamage(Id(_masterLow, MasterName)), null, null, target: OvrName, inPlace: true, acknowledge: true);
        Assert.True(o.Success, o.Error);
        Assert.Equal(55, Damage(_ovr, _masterLow));
    }
}
