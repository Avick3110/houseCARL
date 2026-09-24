using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Every outcome of the four record lanes that consulted a build carries that build's epoch: remove, in-place apply,
/// forward and create. Stryker row T40 (dev/plans/STRYKER_WRITE_PATH_2026-09-23.md).
/// </summary>
[Trait("tier", "integration")]
public sealed class WritePatchEpochTests : IDisposable
{
    const string TargetName = "HcWpEpochTarget.esp";

    readonly WritePathRig _rig = new();
    readonly LoadOrderResolver _order;
    readonly string _targetPath;
    readonly FormKey _weapon, _ownWeapon;

    public WritePatchEpochTests()
    {
        var master = new SkyrimMod(new ModKey("HcWpEpochMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var w = master.Weapons.AddNew();
        w.EditorID = "HcWpEpochSword";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        _weapon = w.FormKey;
        var masterPath = _rig.Write(master);

        var target = new SkyrimMod(ModKey.FromFileName(TargetName), SkyrimRelease.SkyrimSE);
        var own = target.Weapons.AddNew();
        own.EditorID = "HcWpEpochOwnSword";
        own.BasicStats = new WeaponBasicStats { Damage = 3, Weight = 1 };
        _ownWeapon = own.FormKey;
        _targetPath = _rig.Write(target, "target", master);

        _order = _rig.Order(masterPath, _targetPath);
    }

    string Epoch => _order.Capture().Stamp.Epoch;

    WritePatchBuilder.CreateOutcome CreateSword(string path) => WritePatchBuilder.CreateRecords(_order, TestCorpus.Rulebook, new[]
    {
        new WritePatchBuilder.CreateSpec { RecordType = "Weapon", EditorId = "HcWpEpochNew", Edits = Array.Empty<WriteRequest>() },
    }, path, extend: false);

    [Fact]
    public void ACreateCarriesTheEpoch()
    {
        var o = CreateSword(_rig.Out("HcWpEpochCreate.esp"));
        Assert.True(o.Success, o.Error);
        Assert.Equal(Epoch, o.Epoch);
    }

    [Fact]
    public void ARemoveCarriesTheEpoch()
    {
        var path = _rig.Out("HcWpEpochRemove.esp");
        var created = CreateSword(path);
        Assert.True(created.Success, created.Error);

        var o = WritePatchBuilder.RemoveRecords(_order, new[] { created.Created[0].FormKey }, path);
        Assert.True(o.Success, o.Error);
        Assert.Equal(Epoch, o.Epoch);
    }

    [Fact]
    public void AnInPlaceApplyCarriesTheEpoch()
    {
        var o = WritePatchBuilder.ApplyInPlace(_order, TestCorpus.Rulebook,
            new[] { WritePathRig.Set(_ownWeapon, "BasicStats.Damage", "42") }, _targetPath, TargetName);
        Assert.True(o.Success, o.Error);
        Assert.Equal(Epoch, o.Epoch);
    }

    [Fact]
    public void AForwardCarriesTheEpoch()
    {
        var o = WritePatchBuilder.ForwardRecords(_order,
            new[] { new WritePatchBuilder.ForwardSpec { Target = _weapon, FromPlugin = "HcWpEpochMaster.esm" } },
            _rig.Out("HcWpEpochForward.esp"), extend: false, sourceParam: "source");
        Assert.True(o.Success, o.Error);
        Assert.Equal(Epoch, o.Epoch);
    }

    public void Dispose() => _rig.Dispose();
}
