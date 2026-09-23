using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The patch-lane dry run: the in-memory read-back only when asked, and the master list it predicts — the overridden
/// record's plugin and a plugin the edit links into, in load order, with two healthy masters not refused. Stryker row
/// T34 (dev/plans/STRYKER_WRITE_PATH_2026-09-23.md).
/// </summary>
[Trait("tier", "integration")]
public sealed class WritePatchDryRunTests : IClassFixture<WritePathCorpus>, IDisposable
{
    const string Linked = "HcWpDryLinked.esm";
    const string Master = "HcWpDryMaster.esm";

    readonly WritePathCorpus _corpus;
    readonly WritePathRig _rig = new();
    readonly LoadOrderResolver _order;
    readonly FormKey _weapon, _keyword;

    public WritePatchDryRunTests(WritePathCorpus corpus)
    {
        _corpus = corpus;
        // The linked plugin loads FIRST, so load order and the order the edit meets them in differ.
        var linked = new SkyrimMod(ModKey.FromFileName(Linked), SkyrimRelease.SkyrimSE);
        var k = linked.Keywords.AddNew();
        k.EditorID = "HcWpDryKeyword";
        _keyword = k.FormKey;

        var master = new SkyrimMod(ModKey.FromFileName(Master), SkyrimRelease.SkyrimSE);
        var w = master.Weapons.AddNew();
        w.EditorID = "HcWpDrySword";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        _weapon = w.FormKey;

        _order = _rig.Order(_rig.Write(linked), _rig.Write(master));
    }

    WritePatchBuilder.PatchOutcome DryRun(bool fullReadback, params WritePatchBuilder.PatchEdit[] edits)
        => WritePatchBuilder.Apply(_order, _corpus.Rulebook(), edits, _rig.Out("HcWpDryOut.esp"), extend: false,
            fullReadback: fullReadback, dryRun: true);

    WritePatchBuilder.PatchEdit Damage() => WritePathRig.Set(_weapon, "BasicStats.Damage", "42");

    [Fact]
    public void ADryRunReturnsTheReadBackWhenAsked()
    {
        var o = DryRun(fullReadback: true, Damage());
        Assert.True(o.Success, o.Error);
        Assert.NotNull(o.ReadBack);
    }

    [Fact]
    public void ADryRunReturnsNoReadBackWhenNotAsked()
    {
        var o = DryRun(fullReadback: false, Damage());
        Assert.True(o.Success, o.Error);
        Assert.Null(o.ReadBack);
    }

    /// <summary>The overridden weapon's plugin and the keyword's plugin, both healthy, in load order.</summary>
    [Fact]
    public void TheWouldBeMastersAreTheOverriddenAndTheLinkedPluginInLoadOrder()
    {
        var o = DryRun(fullReadback: false, new WritePatchBuilder.PatchEdit
        {
            Target = _weapon, Path = new[] { "Keywords" }, Verb = "Add", Value = _keyword.ToString(),
        });
        Assert.True(o.Success, o.Error);
        Assert.Equal(new[] { Linked, Master }, o.Masters);
    }

    public void Dispose() => _rig.Dispose();
}
