using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Removing a record whose concrete class is a subclass of its flat group's T (a GlobalShort in the Global group, a
/// GameSettingString in the GameSetting group) really removes it, on the in-place and the patch lane. Mutagen's typed
/// Remove with the subclass type silently removes nothing, so both lanes route through <c>RemovalTypeFor</c>.
/// Each test copies a pristine user plugin into its own folder.
/// </summary>
[Trait("tier", "integration")]
public sealed class SubclassRemoveTests : IDisposable
{
    const string MasterName = "HcSubRmMaster.esm";
    const string UserName = "HcSubRmUser.esp";

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-subclass-remove-" + Guid.NewGuid().ToString("N"));
    readonly string _master;
    readonly string _full;
    readonly string _gOnly;
    readonly FormKey _glob, _gmst, _weap1, _weap2;

    public SubclassRemoveTests()
    {
        _master = Path.Combine(_dir, MasterName);
        _full = Path.Combine(_dir, "pristine-full", UserName);
        _gOnly = Path.Combine(_dir, "pristine-gonly", UserName);
        Directory.CreateDirectory(Path.GetDirectoryName(_full)!);
        Directory.CreateDirectory(Path.GetDirectoryName(_gOnly)!);

        var m = new SkyrimMod(new ModKey("HcSubRmMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var g = new GlobalShort(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcSR_Glob", Data = 0 };
        m.Globals.Add(g);
        var gmst = new GameSettingString(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "sHcSR_Gmst", Data = "base" };
        m.GameSettings.Add(gmst);
        var w1 = m.Weapons.AddNew(); w1.EditorID = "HcSR_Weap1"; w1.BasicStats = new WeaponBasicStats { Damage = 10 };
        var w2 = m.Weapons.AddNew(); w2.EditorID = "HcSR_Weap2"; w2.BasicStats = new WeaponBasicStats { Damage = 5 };
        _glob = g.FormKey; _gmst = gmst.FormKey; _weap1 = w1.FormKey; _weap2 = w2.FormKey;
        m.BeginWrite.ToPath(_master).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();

        using var mOv = SkyrimMod.CreateFromBinaryOverlay(_master, SkyrimRelease.SkyrimSE);
        var full = new SkyrimMod(new ModKey("HcSubRmUser", ModType.Plugin), SkyrimRelease.SkyrimSE);
        ((GlobalShort)full.Globals.GetOrAddAsOverride(mOv.Globals.First(x => x.FormKey == _glob))).Data = 1;
        ((GameSettingString)full.GameSettings.GetOrAddAsOverride(mOv.GameSettings.First(x => x.FormKey == _gmst))).Data = "over";
        full.Weapons.GetOrAddAsOverride(mOv.Weapons.First(x => x.FormKey == _weap1)).BasicStats!.Damage = 20;
        full.Weapons.GetOrAddAsOverride(mOv.Weapons.First(x => x.FormKey == _weap2)).BasicStats!.Damage = 25;
        full.BeginWrite.ToPath(_full).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();

        var gOnly = new SkyrimMod(new ModKey("HcSubRmUser", ModType.Plugin), SkyrimRelease.SkyrimSE);
        ((GlobalShort)gOnly.Globals.GetOrAddAsOverride(mOv.Globals.First(x => x.FormKey == _glob))).Data = 1;
        gOnly.BeginWrite.ToPath(_gOnly).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    // Probe arm A: in-place GlobalShort remove while the origin master stays referenced.
    [Fact]
    public void AnInPlaceGlobalShortRemoveTakesTheRecordOutAndKeepsTheReferencedMaster()
    {
        var path = Fresh("A", _full);
        using var r = LoadOrderResolver.Build(new[] { _master, path });

        var o = WritePatchBuilder.RemoveRecordsInPlace(r, new[] { _glob }, path, UserName);

        Assert.True(o.Success, o.Error);
        Assert.False(Present(path, _glob));
        Assert.True(Present(path, _weap1));
        Assert.Contains(MasterName, Masters(path), StringComparer.OrdinalIgnoreCase);
    }

    // Probe arm B: in-place GlobalShort remove as the file's only record prunes the master.
    [Fact]
    public void AnInPlaceGlobalShortRemoveOfTheOnlyRecordPrunesTheMaster()
    {
        var path = Fresh("B", _gOnly);
        using var r = LoadOrderResolver.Build(new[] { _master, path });

        var o = WritePatchBuilder.RemoveRecordsInPlace(r, new[] { _glob }, path, UserName);

        Assert.True(o.Success, o.Error);
        Assert.False(Present(path, _glob));
        Assert.Equal(0, o.RemainingRecords);
        Assert.DoesNotContain(MasterName, Masters(path), StringComparer.OrdinalIgnoreCase);
    }

    // Probe arm C: in-place Weapon remove with the origin master retained (the retention control).
    [Fact]
    public void AnInPlaceWeaponRemoveWithTheMasterRetainedTakesTheRecordOut()
    {
        var path = Fresh("C", _full);
        using var r = LoadOrderResolver.Build(new[] { _master, path });

        var o = WritePatchBuilder.RemoveRecordsInPlace(r, new[] { _weap2 }, path, UserName);

        Assert.True(o.Success, o.Error);
        Assert.False(Present(path, _weap2));
        Assert.Contains(MasterName, Masters(path), StringComparer.OrdinalIgnoreCase);
    }

    // Probe arm D: the patch lane removes a GlobalShort too.
    [Fact]
    public void ThePatchLaneRemovesAGlobalShort()
    {
        var path = Fresh("D", _full);
        using var r = LoadOrderResolver.Build(new[] { _master, path });

        var o = WritePatchBuilder.RemoveRecords(r, new[] { _glob }, path);

        Assert.True(o.Success, o.Error);
        Assert.False(Present(path, _glob));
    }

    // Probe arm E: in-place GameSettingString remove, the second abstract flat group.
    [Fact]
    public void AnInPlaceGameSettingStringRemoveTakesTheRecordOut()
    {
        var path = Fresh("E", _full);
        using var r = LoadOrderResolver.Build(new[] { _master, path });

        var o = WritePatchBuilder.RemoveRecordsInPlace(r, new[] { _gmst }, path, UserName);

        Assert.True(o.Success, o.Error);
        Assert.False(Present(path, _gmst));
    }

    // Probe arm F: RemovalTypeFor routes a GlobalShort to its group's T and a Weapon to its own type.
    [Fact]
    public void RemovalTypeForRoutesASubclassToItsGroupsType()
    {
        var mod = SkyrimMod.CreateFromBinary(_full, SkyrimRelease.SkyrimSE);

        Assert.Equal(typeof(Global), WriteEngine.RemovalTypeFor(mod.EnumerateMajorRecords().First(x => x.FormKey == _glob)));
        Assert.Equal(typeof(GameSetting), WriteEngine.RemovalTypeFor(mod.EnumerateMajorRecords().First(x => x.FormKey == _gmst)));
        Assert.Equal(typeof(Weapon), WriteEngine.RemovalTypeFor(mod.EnumerateMajorRecords().First(x => x.FormKey == _weap1)));
    }

    // Probe arm F: Mutagen's Remove with the subclass type still removes nothing (if this fails, re-evaluate RemovalTypeFor).
    [Fact]
    public void MutagensRemoveWithTheSubclassTypeStillRemovesNothing()
    {
        var mod = SkyrimMod.CreateFromBinary(_full, SkyrimRelease.SkyrimSE);

        ((IMajorRecordEnumerable)mod).Remove(_glob, typeof(GlobalShort), throwIfUnknown: true);
        Assert.Contains(mod.EnumerateMajorRecords(), x => x.FormKey == _glob);

        ((IMajorRecordEnumerable)mod).Remove(_glob, typeof(Global), throwIfUnknown: true);
        Assert.DoesNotContain(mod.EnumerateMajorRecords(), x => x.FormKey == _glob);
    }

    string Fresh(string tag, string pristine)
    {
        var dir = Path.Combine(_dir, tag);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, UserName);
        File.Copy(pristine, path, overwrite: true);
        return path;
    }

    static bool Present(string path, FormKey fk)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return ov.EnumerateMajorRecords().Any(x => x.FormKey == fk);
    }

    static List<string> Masters(string path)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return ov.ModHeader.MasterReferences.Select(m => m.Master.FileName.ToString()).ToList();
    }
}
