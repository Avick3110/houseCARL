using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The forward lane: which sources it accepts and refuses, and what its outcome reports — the off-order source it read,
/// a replaced record, each record forwarded, and the read-back only when asked. Stryker rows T43 and T44
/// (dev/plans/STRYKER_WRITE_PATH_2026-09-23.md).
/// </summary>
[Trait("tier", "integration")]
public sealed class WritePatchForwardTests : IDisposable
{
    const string Master = "HcWpFwdMaster.esm";
    const string Winner = "HcWpFwdWinner.esp";
    const string OffOrder = "HcWpFwdLoose.esp";
    const string Inactive = "HcWpFwdInactive.esm";

    readonly WritePathRig _rig = new();
    readonly SkyrimMod _master;
    readonly string _masterPath, _winnerPath;
    readonly FormKey _sword, _axe;

    public WritePatchForwardTests()
    {
        _master = new SkyrimMod(ModKey.FromFileName(Master), SkyrimRelease.SkyrimSE);
        _sword = Weapon(_master, "HcWpFwdSword", 10).FormKey;
        _axe = Weapon(_master, "HcWpFwdAxe", 11).FormKey;
        _masterPath = _rig.Write(_master);

        // An active plugin overriding the sword, so it is the sword's winner.
        _winnerPath = _rig.Write(Overriding(Winner, _sword, 20), "winner", _master);
    }

    static Weapon Weapon(SkyrimMod mod, string edid, ushort damage)
    {
        var w = mod.Weapons.AddNew();
        w.EditorID = edid;
        w.BasicStats = new WeaponBasicStats { Damage = damage, Weight = 1 };
        return w;
    }

    static SkyrimMod Overriding(string name, FormKey target, ushort damage, FormKey? also = null)
    {
        var mod = new SkyrimMod(ModKey.FromFileName(name), SkyrimRelease.SkyrimSE);
        foreach (var fk in also is { } a ? new[] { target, a } : new[] { target })
            mod.Weapons.Add(new Weapon(fk, SkyrimRelease.SkyrimSE)
                { EditorID = "Over" + fk.ID, BasicStats = new WeaponBasicStats { Damage = damage, Weight = 1 } });
        return mod;
    }

    LoadOrderResolver Order() => _rig.Order(_masterPath, _winnerPath);

    /// <summary>A plugin file outside the load order, as the service would have located it.</summary>
    WritePatchBuilder.OffOrderForwardSource Loose(string path)
    {
        var ov = _rig.Open(path);
        return new WritePatchBuilder.OffOrderForwardSource
        {
            Plugin = Path.GetFileName(path), Path = path, Where = "a test folder",
            Bodies = ov.EnumerateMajorRecords().ToDictionary(r => r.FormKey, r => (IMajorRecordGetter)r),
            Overlay = ov,
        };
    }

    static WritePatchBuilder.ForwardSpec Spec(FormKey target, string from) => new() { Target = target, FromPlugin = from };

    WritePatchBuilder.ForwardOutcome Forward(LoadOrderResolver order, string outPath, bool extend = false, bool readback = false,
        WritePatchBuilder.OffOrderForwardSource? offOrder = null, params WritePatchBuilder.ForwardSpec[] specs)
        => WritePatchBuilder.ForwardRecords(order, specs, outPath, extend, "source", fullReadback: readback, offOrder: offOrder);

    ushort DamageOnDisk(string path, FormKey fk) => _rig.Open(path).Weapons.Single(w => w.FormKey == fk).BasicStats!.Damage;

    // ---- T43: sources ----

    /// <summary>The source is compared to the output by full path, so a loose file sharing the output's NAME is a
    /// different file and is accepted.</summary>
    [Fact]
    public void AnOffOrderFileWithTheOutputsNameAtAnotherPathIsAccepted()
    {
        var loose = _rig.Write(Overriding("HcWpFwdSame.esp", _sword, 30), "loose", _master);
        var o = Forward(Order(), _rig.Out("HcWpFwdSame.esp"), offOrder: Loose(loose), specs: Spec(_sword, "HcWpFwdSame.esp"));
        Assert.True(o.Success, o.Error);
    }

    [Fact]
    public void ARecordFromAnInactiveOriginIsRefusedNamingItsOrigin()
    {
        var inactive = new SkyrimMod(ModKey.FromFileName(Inactive), SkyrimRelease.SkyrimSE);
        var orphan = Weapon(inactive, "HcWpFwdOrphan", 5).FormKey;
        _rig.Write(inactive, "inactive");
        var loose = _rig.Write(Overriding(OffOrder, orphan, 6), "loose", inactive);

        var o = Forward(Order(), _rig.Out("HcWpFwdOut.esp"), offOrder: Loose(loose), specs: Spec(orphan, OffOrder));

        Assert.False(o.Success);
        Assert.Contains("ORIGINATES", o.Error);
    }

    [Fact]
    public void ForwardingFromTheCurrentWinnerSaysItIsTheWinner()
    {
        var o = Forward(Order(), _rig.Out("HcWpFwdOut.esp"), specs: Spec(_sword, Winner));
        Assert.True(o.Success, o.Error);
        Assert.True(o.Forwarded.Single().WasAlreadyWinner);
    }

    [Fact]
    public void OneBadSpecRefusesTheWholeCall()
    {
        var path = _rig.Out("HcWpFwdOut.esp");
        var o = Forward(Order(), path, specs: new[] { Spec(_sword, Master), Spec(_axe, Winner) });   // the winner plugin never touches the axe
        Assert.False(o.Success);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ASourceNeitherInTheOrderNorLocatedIsRefused()
    {
        var o = Forward(Order(), _rig.Out("HcWpFwdOut.esp"), specs: Spec(_sword, "HcWpFwdNowhere.esp"));
        Assert.False(o.Success);
        Assert.Contains("not in the load order", o.Error);
    }

    [Fact]
    public void ASourceThatIsTheOutputPatchIsRefusedNamingTheOutputPatch()
    {
        var o = Forward(Order(), _rig.Out("HcWpFwdOut.esp"), specs: Spec(_sword, "HcWpFwdOut.esp"));
        Assert.False(o.Success);
        Assert.Contains("output patch", o.Error);
    }

    // ---- T44: outcome ----

    [Fact]
    public void AnOffOrderForwardSaysWhichCopyItRead()
    {
        var loose = _rig.Write(Overriding(OffOrder, _sword, 30), "loose", _master);
        var o = Forward(Order(), _rig.Out("HcWpFwdOut.esp"), offOrder: Loose(loose), specs: Spec(_sword, OffOrder));
        Assert.True(o.Success, o.Error);
        Assert.Equal(loose, o.OffOrderSource?.Path);
    }

    [Fact]
    public void AnInOrderForwardCarriesNoOffOrderSource()
    {
        var loose = _rig.Write(Overriding(OffOrder, _sword, 30), "loose", _master);
        var o = Forward(Order(), _rig.Out("HcWpFwdOut.esp"), offOrder: Loose(loose), specs: Spec(_sword, Master));
        Assert.True(o.Success, o.Error);
        Assert.Null(o.OffOrderSource);
    }

    [Fact]
    public void AMixedForwardSaysWhichOffOrderCopyItRead()
    {
        var loose = _rig.Write(Overriding(OffOrder, _sword, 30), "loose", _master);
        var o = Forward(Order(), _rig.Out("HcWpFwdOut.esp"), offOrder: Loose(loose),
            specs: new[] { Spec(_sword, OffOrder), Spec(_axe, Master) });
        Assert.True(o.Success, o.Error);
        Assert.Equal(loose, o.OffOrderSource?.Path);
    }

    [Fact]
    public void AForwardIntoAPatchCarryingTheRecordReplacesIt()
    {
        var order = Order();
        var path = _rig.Out("HcWpFwdOut.esp");
        Assert.True(Forward(order, path, specs: Spec(_sword, Master)).Success);

        var o = Forward(order, path, extend: true, specs: Spec(_sword, Winner));

        Assert.True(o.Success, o.Error);
        Assert.True(o.Forwarded.Single().ReplacedExisting);
        Assert.Equal(20, DamageOnDisk(path, _sword));
    }

    [Fact]
    public void AFreshForwardReplacesNothing()
    {
        var o = Forward(Order(), _rig.Out("HcWpFwdOut.esp"), specs: Spec(_sword, Master));
        Assert.True(o.Success, o.Error);
        Assert.False(o.Forwarded.Single().ReplacedExisting);
    }

    [Fact]
    public void TheOutcomeListsEachRecordForwarded()
    {
        var o = Forward(Order(), _rig.Out("HcWpFwdOut.esp"), specs: new[] { Spec(_sword, Master), Spec(_axe, Master) });
        Assert.True(o.Success, o.Error);
        Assert.Equal(new[] { _sword, _axe }, o.Forwarded.Select(f => f.Target));
    }

    /// <summary>The patch is itself an active plugin in the order the forward reads, so its serialize must not hold
    /// an overlay on it. No forward read opens the patch through the session, so this pins the master-set half.</summary>
    [Fact]
    public void AForwardIntoAnActivePatchWrites()
    {
        var patchPath = _rig.Write(Overriding("HcWpFwdActive.esp", _axe, 40), "active", _master);
        var order = _rig.Order(_masterPath, _winnerPath, patchPath);

        var o = Forward(order, patchPath, extend: true, specs: Spec(_sword, Winner));

        Assert.True(o.Success, o.Error);
        Assert.Equal(20, DamageOnDisk(patchPath, _sword));
    }

    [Fact]
    public void AForwardReadsBackOnlyWhenAsked()
    {
        var order = Order();
        Assert.Null(Forward(order, _rig.Out("HcWpFwdOut.esp"), specs: Spec(_sword, Master)).ReadBack);
        Assert.NotNull(Forward(order, _rig.Out("HcWpFwdOut.esp"), readback: true, specs: Spec(_sword, Master)).ReadBack);
    }

    public void Dispose() => _rig.Dispose();
}
