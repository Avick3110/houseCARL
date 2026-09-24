using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// No write lane holds a plugin open once it returns: after apply, in-place apply, remove, forward, compact, merge, the
/// merge donor scan and the originating-keys read, the output and every plugin the call read open exclusively. Stryker
/// row T35 (dev/plans/STRYKER_WRITE_PATH_2026-09-23.md).
/// </summary>
[Trait("tier", "integration")]
public sealed class WritePatchHandleTests : IDisposable
{
    const string TargetName = "HcWpHoldTarget.esp";

    readonly WritePathRig _rig = new();
    readonly SkyrimMod _master, _second;
    readonly string _masterPath, _secondPath, _targetPath;
    readonly FormKey _sword, _keyword, _secondKeyword, _ownSword;

    public WritePatchHandleTests()
    {
        _master = new SkyrimMod(ModKey.FromFileName("HcWpHoldMaster.esm"), SkyrimRelease.SkyrimSE);
        var k = _master.Keywords.AddNew();
        k.EditorID = "HcWpHoldKeyword";
        _keyword = k.FormKey;
        var w = _master.Weapons.AddNew();
        w.EditorID = "HcWpHoldSword";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        _sword = w.FormKey;
        _masterPath = _rig.Write(_master);

        // A second master, so a donor's header carries two and the serialize has to sort them.
        _second = new SkyrimMod(ModKey.FromFileName("HcWpHoldSecond.esm"), SkyrimRelease.SkyrimSE);
        var k2 = _second.Keywords.AddNew();
        k2.EditorID = "HcWpHoldSecondKeyword";
        _secondKeyword = k2.FormKey;
        _secondPath = _rig.Write(_second);

        var target = new SkyrimMod(ModKey.FromFileName(TargetName), SkyrimRelease.SkyrimSE);
        var own = target.Weapons.AddNew();
        own.EditorID = "HcWpHoldOwnSword";
        own.BasicStats = new WeaponBasicStats { Damage = 3, Weight = 1 };
        _ownSword = own.FormKey;
        _targetPath = _rig.Write(target, "target", _master);
    }

    LoadOrderResolver Order() => _rig.Order(_masterPath, _targetPath);

    static void NoneHeld(params string[] paths)
    {
        foreach (var p in paths) WritePathRig.AssertNotHeld(p);
    }

    /// <summary>A plugin of its own weapons, each keyworded from both masters so both are needed.</summary>
    string Donor(string name, params uint[] ids)
    {
        var mod = new SkyrimMod(ModKey.FromFileName(name), SkyrimRelease.SkyrimSE);
        foreach (var id in ids)
            mod.Weapons.Add(new Weapon(new FormKey(mod.ModKey, id), SkyrimRelease.SkyrimSE)
            {
                EditorID = name[..^4] + id,
                Keywords = new Noggog.ExtendedList<IFormLinkGetter<IKeywordGetter>>
                    { new FormLink<IKeywordGetter>(_keyword), new FormLink<IKeywordGetter>(_secondKeyword) },
            });
        return _rig.Write(mod, "donors", _master, _second);
    }

    string? MasterPath(string name) => name switch
    {
        "HcWpHoldMaster.esm" => _masterPath,
        "HcWpHoldSecond.esm" => _secondPath,
        _ => null,
    };

    [Fact]
    public void ApplyReleasesItsOutput()
    {
        var path = _rig.Out("HcWpHoldApply.esp");
        var o = WritePatchBuilder.Apply(Order(), TestCorpus.Rulebook(), new[] { WritePathRig.Set(_sword, "BasicStats.Damage", "42") }, path, extend: false);
        Assert.True(o.Success, o.Error);
        NoneHeld(path, _masterPath);
    }

    [Fact]
    public void InPlaceApplyReleasesItsTarget()
    {
        var o = WritePatchBuilder.ApplyInPlace(Order(), TestCorpus.Rulebook(),
            new[] { WritePathRig.Set(_ownSword, "BasicStats.Damage", "42") }, _targetPath, TargetName);
        Assert.True(o.Success, o.Error);
        NoneHeld(_targetPath, _masterPath);
    }

    [Fact]
    public void RemoveReleasesItsOutput()
    {
        var order = Order();
        var path = _rig.Out("HcWpHoldRemove.esp");
        var created = WritePatchBuilder.CreateRecords(order, TestCorpus.Rulebook(), new[]
        {
            new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcWpHoldGone", Edits = Array.Empty<WriteRequest>() },
            new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcWpHoldKept", Edits = Array.Empty<WriteRequest>() },
        }, path, extend: false);
        Assert.True(created.Success, created.Error);

        var o = WritePatchBuilder.RemoveRecords(order, new[] { created.Created[0].FormKey }, path);
        Assert.True(o.Success, o.Error);
        NoneHeld(path, _masterPath);
    }

    [Fact]
    public void ForwardReleasesItsOutput()
    {
        var path = _rig.Out("HcWpHoldForward.esp");
        var o = WritePatchBuilder.ForwardRecords(Order(),
            new[] { new WritePatchBuilder.ForwardSpec { Target = _sword, FromPlugin = "HcWpHoldMaster.esm" } }, path, extend: false, sourceParam: "source");
        Assert.True(o.Success, o.Error);
        NoneHeld(path, _masterPath);
    }

    [Fact]
    public void CompactReleasesItsSourceOutputAndMasters()
    {
        var src = Donor("HcWpHoldCompact.esp", 0x900);
        var key = ModKey.FromFileName("HcWpHoldCompact.esp");
        var outPath = _rig.Out("HcWpHoldCompact.esp");
        var dict = new Dictionary<FormKey, FormKey> { [new FormKey(key, 0x900)] = new FormKey(key, 0x800) };

        var r = WritePatchBuilder.CompactBuild(src, key, dict, MasterPath, outPath, esl: false, floor: 0x800, dataDir: null);

        Assert.True(r.Success, r.Error);
        NoneHeld(src, outPath, _masterPath, _secondPath);
    }

    [Fact]
    public void MergeReleasesItsDonorsOutputAndMasters()
    {
        var d1 = Donor("HcWpHoldDonorA.esp", 0x800);
        var d2 = Donor("HcWpHoldDonorB.esp", 0x800);
        var outKey = ModKey.FromFileName("HcWpHoldMerged.esp");
        var outPath = _rig.Out("HcWpHoldMerged.esp");
        var ka = ModKey.FromFileName("HcWpHoldDonorA.esp");
        var kb = ModKey.FromFileName("HcWpHoldDonorB.esp");
        var dict = new Dictionary<FormKey, FormKey>
        {
            [new FormKey(ka, 0x800)] = new FormKey(outKey, 0x800),
            [new FormKey(kb, 0x800)] = new FormKey(outKey, 0x801),
        };

        var r = WritePatchBuilder.MergeBuild(new[] { ("HcWpHoldDonorA.esp", d1, ka), ("HcWpHoldDonorB.esp", d2, kb) },
            outKey, dict, new[] { "HcWpHoldMaster.esm", "HcWpHoldSecond.esm" }, MasterPath, outPath, dataDir: null);

        Assert.True(r.Success, r.Error);
        NoneHeld(d1, d2, outPath, _masterPath, _secondPath);
    }

    [Fact]
    public void TheMergeDonorScanReleasesTheDonor()
    {
        var d = Donor("HcWpHoldScan.esp", 0x800);
        var key = ModKey.FromFileName("HcWpHoldScan.esp");
        Assert.True(WritePatchBuilder.TryScanMergeDonor(d, key, new HashSet<ModKey> { key }, out _, out var error), error);
        NoneHeld(d);
    }

    [Fact]
    public void TheOriginatingKeysReadReleasesThePlugin()
    {
        var d = Donor("HcWpHoldKeys.esp", 0x800);
        Assert.True(WritePatchBuilder.TryReadOriginatingKeys(d, ModKey.FromFileName("HcWpHoldKeys.esp"), out var keys, out var error), error);
        Assert.Single(keys);
        NoneHeld(d);
    }

    public void Dispose() => _rig.Dispose();
}
