using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;
using static HousecarlMcpTests.NestedCreateRig;

namespace HousecarlMcpTests;

/// <summary>An extend edit whose target the patch itself defines resolves to that record, beside a load-order target in
/// the same call; a target in neither place is refused naming both; an override the patch only carries, of a record
/// whose plugin has left the order, is still refused. Migrated from the nested-create-guard probe (arm EXTEND-EDIT).</summary>
[Trait("tier", "integration")]
public sealed class ExtendEditPatchTargetTests : IDisposable
{
    readonly NestedCreateRig _w = new();
    readonly string _patch;
    readonly FormKey _misc;

    public ExtendEditPatchTargetTests()
    {
        _patch = _w.Rig.Out("HcNcExtEdit.esp");
        var o = _w.CreateAt(_patch, false, Spec("MiscItem", "HcNcExtMisc"));
        Assert.True(o.Success, o.Error);
        _misc = o.Created[0].FormKey;
    }

    WritePatchBuilder.PatchOutcome Extend(LoadOrderResolver order, params WritePatchBuilder.PatchEdit[] edits)
        => WritePatchBuilder.Apply(order, TestCorpus.Rulebook, edits, _patch, extend: true);

    // EXTEND-EDIT: a record the patch defines and a master record, edited in one extend call, both land.
    [Fact]
    public void AnExtendEditReachesARecordThePatchDefinesBesideALoadOrderTarget()
    {
        var o = Extend(_w.Order, WritePathRig.Set(_misc, "Value", "123"), WritePathRig.Set(_w.Topic, "Priority", "50"));
        Assert.True(o.Success, o.Error);
        var mod = _w.Open(_patch);
        Assert.Equal(123u, mod.MiscItems.Single(x => x.FormKey == _misc).Value);
        Assert.Equal(50f, mod.DialogTopics.Single(x => x.FormKey == _w.Topic).Priority);
    }

    // EXTEND-EDIT: a target in neither the load order nor the patch refuses naming both places searched.
    [Fact]
    public void AnExtendEditOfAnAbsentTargetIsRefusedNamingBothPlaces()
    {
        var miss = new FormKey(new ModKey("HcNcExtEdit", ModType.Plugin), 0xEEE);
        var o = Extend(_w.Order, WritePathRig.Set(miss, "Value", "1"));
        Assert.False(o.Success);
        Assert.Contains("load order", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("the patch being extended", o.Error, StringComparison.OrdinalIgnoreCase);
    }

    // EXTEND-EDIT: an override the patch carries of a record whose plugin left the order is refused ('OVERRIDES').
    [Fact]
    public void AnExtendEditOfACarriedOverrideWhosePluginLeftTheOrderIsRefused()
    {
        var m2 = new SkyrimMod(ModKey.FromFileName("HcNcExtM2.esm"), SkyrimRelease.SkyrimSE);
        var misc = m2.MiscItems.AddNew(); misc.EditorID = "HcNcExtM2Misc"; misc.Value = 7;
        var m2Path = _w.Rig.Write(m2, "m2");
        var carry = Extend(_w.Rig.Order(_w.MasterPath, m2Path), WritePathRig.Set(misc.FormKey, "Value", "9"));
        Assert.True(carry.Success, carry.Error);
        var stale = Extend(_w.Order, WritePathRig.Set(misc.FormKey, "Value", "1"));
        Assert.False(stale.Success);
        Assert.Contains("OVERRIDES", stale.Error, StringComparison.Ordinal);
    }

    public void Dispose() => _w.Dispose();
}
