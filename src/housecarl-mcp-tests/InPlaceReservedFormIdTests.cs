using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The in-place lanes refuse a plugin with a pre-1.71 header that defines records of its own below 0x800, before
/// writing, the dry run and the consent prompt (#1049); the default lane still writes an override of such a record; a plugin
/// whose only sub-0x800 records are overrides of its master, or whose 1.71 header allows the low range, is not refused.</summary>
[Trait("tier", "integration")]
public sealed class InPlaceReservedFormIdTests : IDisposable
{
    const string MasterName = "HcResMaster.esm";
    const string OwnName = "HcResOwn.esp";
    const string OvrName = "HcResOvr.esp";
    const string NewName = "HcResNew.esp";
    const string Below = "below 0x800";

    readonly string _root, _instance, _mods, _own, _ovr, _new;
    readonly FormKey _masterLow, _ownLow, _newLow;
    readonly LoadOrderService _svc;

    public InPlaceReservedFormIdTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "hc-inplace-reserved-" + Guid.NewGuid().ToString("N"));
        _instance = Path.Combine(_root, "instance");
        _mods = Path.Combine(_instance, "mods");
        var profiles = Path.Combine(_instance, "profiles", "Default");
        Directory.CreateDirectory(profiles);
        Directory.CreateDirectory(Path.Combine(_root, "game", "Data"));
        File.WriteAllText(Path.Combine(_instance, "ModOrganizer.ini"),
            "[General]\r\ngameName=Skyrim Special Edition\r\nselected_profile=@ByteArray(Default)\r\ngamePath=@ByteArray("
            + Path.Combine(_root, "game").Replace(@"\", @"\\") + ")\r\n");

        string Dir(string mod) { var d = Path.Combine(_mods, mod); Directory.CreateDirectory(d); return d; }
        var master = Path.Combine(Dir("MasterMod"), MasterName);
        _own = Path.Combine(Dir("OwnMod"), OwnName);
        _ovr = Path.Combine(Dir("OvrMod"), OvrName);
        _new = Path.Combine(Dir("NewMod"), NewName);

        var mKey = new ModKey("HcResMaster", ModType.Master);
        var m = new SkyrimMod(mKey, SkyrimRelease.SkyrimSE);
        _masterLow = new FormKey(mKey, 0x123);
        m.Weapons.Add(new Weapon(_masterLow, SkyrimRelease.SkyrimSE) { EditorID = "HcRes_MasterLow", BasicStats = new WeaponBasicStats { Damage = 10 } });
        m.BeginWrite.ToPath(master).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoCheckIfLowerRangeDisallowed().Write();
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(master, SkyrimRelease.SkyrimSE);
        var masterWeap = mOv.Weapons.Single();

        // Defines a weapon of its own at 0x456 under a 1.70 header: the shape of a vanilla or Creation Club master.
        _ownLow = WriteOwnLow("HcResOwn", _own, 7, 1.70f, mOv, masterWeap);
        // The same shape under a 1.71 header, which may use the low range.
        _newLow = WriteOwnLow("HcResNew", _new, 8, 1.71f, mOv, masterWeap);

        // Its only sub-0x800 record is the override of the master's 0x123; its own record sits at 0x900.
        var vKey = new ModKey("HcResOvr", ModType.Plugin);
        var v = new SkyrimMod(vKey, SkyrimRelease.SkyrimSE);
        v.Weapons.GetOrAddAsOverride(masterWeap).BasicStats!.Damage = 30;
        v.Keywords.Add(new Keyword(new FormKey(vKey, 0x900), SkyrimRelease.SkyrimSE) { EditorID = "HcRes_OvrKw" });
        v.BeginWrite.ToPath(_ovr).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();

        var plugins = new[] { MasterName, OwnName, OvrName, NewName };
        File.WriteAllText(Path.Combine(profiles, "loadorder.txt"), "# header\r\n" + string.Join("\r\n", plugins) + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "plugins.txt"), string.Join("\r\n", plugins.Select(p => "*" + p)) + "\r\n");
        File.WriteAllText(Path.Combine(profiles, "modlist.txt"), "# header\r\n+NewMod\r\n+OvrMod\r\n+OwnMod\r\n+MasterMod\r\n");
        _svc = LoadOrderService.WithInstance(_instance, 0, new UserConfigStore(Path.Combine(_root, "houseCARL.user.json")));
        _svc.Stats();
    }

    static FormKey WriteOwnLow(string name, string path, int damage, float version, ISkyrimModGetter mOv, IWeaponGetter masterWeap)
    {
        var key = new ModKey(name, ModType.Plugin);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.Stats.Version = version;
        var low = new FormKey(key, 0x456);
        mod.Weapons.Add(new Weapon(low, SkyrimRelease.SkyrimSE) { EditorID = name + "_Low", BasicStats = new WeaponBasicStats { Damage = (ushort)damage } });
        mod.Weapons.GetOrAddAsOverride(masterWeap).BasicStats!.Damage = 20;
        mod.BeginWrite.ToPath(path).WithLoadOrder(new[] { mOv }).NoCheckIfLowerRangeDisallowed().NoFormIDCompactnessCheck().NoNextFormIDProcessing().Write();
        return low;
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
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
        var o = _svc.ApplyEdits(SetDamage(Id(_ownLow, OwnName)), null, null, target: OwnName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains(Below, o.Error, StringComparison.Ordinal);
        Assert.Contains(LocalizedTargetUnsupportedException.RemedyDefaultLane, o.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(_own));
    }

    [Fact]
    public void AnInPlaceDryRunOnThatPluginIsRefusedNotPreviewed()
    {
        var o = _svc.ApplyEdits(SetDamage(Id(_ownLow, OwnName)), null, null, target: OwnName, inPlace: true, dryRun: true);
        Assert.False(o.Success);
        Assert.False(o.DryRun);
        Assert.Contains(Below, o.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnacknowledgedInPlaceApplyOnThatPluginIsRefusedBeforeTheConsentPrompt()
    {
        var o = _svc.ApplyEdits(SetDamage(Id(_ownLow, OwnName)), null, null, target: OwnName, inPlace: true, acknowledge: false);
        Assert.False(o.Success);
        Assert.False(o.NeedsAcknowledge);
        Assert.Contains(Below, o.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInPlaceCreateIntoThatPluginIsRefused()
    {
        var before = File.ReadAllBytes(_own);
        var o = _svc.InPlaceGuardCreate("Keyword", "HcRes_NewKw", Array.Empty<BulkOp>(), null, null, target: OwnName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains(Below, o.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(_own));
    }

    [Fact]
    public void AnInPlaceRemoveFromThatPluginIsRefusedWithAForwardRemedy()
    {
        var before = File.ReadAllBytes(_own);
        var o = _svc.RemoveRecords(new[] { Id(_masterLow, MasterName) }, null, target: OwnName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains(Below, o.Error, StringComparison.Ordinal);
        Assert.Contains(LocalizedTargetUnsupportedException.RemoveReservedRemedy, o.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(_own));
    }

    [Fact]
    public void TheRemoveRefusalsRemedyForwardingTheMastersVersionToANewPluginWorks()
    {
        var o = _svc.ForwardRecords(new[] { Id(_masterLow, MasterName) }, MasterName, "HcResRevert", null);
        Assert.True(o.Success, o.Error);
        Assert.Equal(10, Damage(o.OutputPath, _masterLow));
    }

    [Fact]
    public void AnInPlaceForwardIntoThatPluginIsRefused()
    {
        var before = File.ReadAllBytes(_own);
        var o = _svc.ForwardRecords(new[] { Id(_masterLow, MasterName) }, MasterName, null, null, target: OwnName, inPlace: true, acknowledge: true);
        Assert.False(o.Success);
        Assert.Contains(Below, o.Error, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(_own));
    }

    [Fact]
    public void TheDefaultLaneStillWritesAnOverrideOfThatPluginsSub0x800Record()
    {
        var o = _svc.ApplyEdits(SetDamage(Id(_ownLow, OwnName)), "HcResPatch", null, inPlace: false);
        Assert.True(o.Success, o.Error);
        Assert.Equal(55, Damage(o.OutputPath, _ownLow));
    }

    [Fact]
    public void APluginWhoseOnlySub0x800RecordsAreOverridesOfItsMasterIsNotRefused()
    {
        var o = _svc.ApplyEdits(SetDamage(Id(_masterLow, MasterName)), null, null, target: OvrName, inPlace: true, acknowledge: true);
        Assert.True(o.Success, o.Error);
        Assert.Equal(55, Damage(_ovr, _masterLow));
    }

    [Fact]
    public void AHeader171PluginWithItsOwnSub0x800RecordIsNotRefusedInPlace()
    {
        var o = _svc.ApplyEdits(SetDamage(Id(_newLow, NewName)), null, null, target: NewName, inPlace: true, acknowledge: true);
        Assert.True(o.Success, o.Error);
        Assert.Equal(55, Damage(_new, _newLow));
    }

    [Fact]
    public void APluginTheCheckCannotReadIsRefusedNotPassed()
    {
        var bad = Path.Combine(_root, "HcResBad.esp");
        File.WriteAllBytes(bad, File.ReadAllBytes(_own).Take(64).Concat(Enumerable.Repeat((byte)0xAB, 256)).ToArray());
        var refusal = ReservedOwnRecords.RefusalFor(bad, "HcResBad.esp", LocalizedTargetUnsupportedException.RemedyDefaultLane);
        Assert.NotNull(refusal);
        Assert.Contains("could not read 'HcResBad.esp'", refusal, StringComparison.Ordinal);
    }
}
