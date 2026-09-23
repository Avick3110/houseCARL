using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The notes a patch-lane write carries beside its result: the master-added re-sort note belongs to a write that
/// EXTENDED an existing file, never to a fresh one, and the link-type note survives when there is no master note to
/// join it with. Stryker rows T28, T36 and T39 (dev/plans/STRYKER_WRITE_PATH_2026-09-23.md).
/// </summary>
[Trait("tier", "integration")]
public sealed class WritePatchNoteTests : IClassFixture<WritePathCorpus>, IDisposable
{
    readonly WritePathCorpus _corpus;
    readonly WritePathRig _rig = new();
    readonly string _masterPath, _linkedPath;
    readonly FormKey _weapon, _keyword;

    public WritePatchNoteTests(WritePathCorpus corpus)
    {
        _corpus = corpus;
        var master = new SkyrimMod(new ModKey("HcWpNoteMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var w = master.Weapons.AddNew();
        w.EditorID = "HcWpNoteSword";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        _weapon = w.FormKey;
        _masterPath = _rig.Write(master);

        var linked = new SkyrimMod(new ModKey("HcWpNoteLinked", ModType.Master), SkyrimRelease.SkyrimSE);
        var k = linked.Keywords.AddNew();
        k.EditorID = "HcWpNoteKeyword";
        _keyword = k.FormKey;
        _linkedPath = _rig.Write(linked);
    }

    WritePatchBuilder.PatchEdit[] Damage() => new[] { WritePathRig.Set(_weapon, "BasicStats.Damage", "42") };

    /// <summary>A patch with no masters, on disk, for the extend lane to grow.</summary>
    string EmptyPatch(string name)
    {
        var path = _rig.Out(name);
        var empty = new SkyrimMod(ModKey.FromFileName(name), SkyrimRelease.SkyrimSE);
        empty.BeginWrite.ToPath(path).WithNoLoadOrder().Write();
        return path;
    }

    // T28: a fresh patch has no before-state, so nothing was "added" to it.
    [Fact]
    public void AFreshApplyCarriesNoMasterAddedNote()
    {
        var order = _rig.Order(_masterPath);
        var o = WritePatchBuilder.Apply(order, _corpus.Rulebook(), Damage(), _rig.Out("HcWpNoteFresh.esp"), extend: false);
        Assert.True(o.Success, o.Error);
        Assert.Null(o.Note);
    }

    [Fact]
    public void AFreshApplyDryRunCarriesNoMasterAddedNote()
    {
        var order = _rig.Order(_masterPath);
        var o = WritePatchBuilder.Apply(order, _corpus.Rulebook(), Damage(), _rig.Out("HcWpNoteDry.esp"), extend: false, dryRun: true);
        Assert.True(o.Success, o.Error);
        Assert.Null(o.Note);
    }

    [Fact]
    public void AFreshForwardCarriesNoMasterAddedNote()
    {
        var order = _rig.Order(_masterPath);
        var specs = new[] { new WritePatchBuilder.ForwardSpec { Target = _weapon, FromPlugin = "HcWpNoteMaster.esm" } };
        var o = WritePatchBuilder.ForwardRecords(order, specs, _rig.Out("HcWpNoteFwd.esp"), extend: false, sourceParam: "source");
        Assert.True(o.Success, o.Error);
        Assert.Null(o.Note);
        var dry = WritePatchBuilder.ForwardRecords(order, specs, _rig.Out("HcWpNoteFwdDry.esp"), extend: false,
            sourceParam: "source", dryRun: true);
        Assert.True(dry.Success, dry.Error);
        Assert.Null(dry.Note);
    }

    // T39: extending a patch that did not master the record's plugin grows its header, and the note says what to do.
    [Fact]
    public void TheMasterAddedNoteSaysToReSortTheLoadOrder()
    {
        var order = _rig.Order(_masterPath);
        var path = EmptyPatch("HcWpNoteGrow.esp");
        var o = WritePatchBuilder.Apply(order, _corpus.Rulebook(), Damage(), path, extend: true);
        Assert.True(o.Success, o.Error);
        Assert.Contains("re-sort your load order", o.Note);
    }

    [Fact]
    public void TheDryRunWouldAddNoteSaysToReSortTheLoadOrder()
    {
        var order = _rig.Order(_masterPath);
        var path = EmptyPatch("HcWpNoteGrowDry.esp");
        var o = WritePatchBuilder.Apply(order, _corpus.Rulebook(), Damage(), path, extend: true, dryRun: true);
        Assert.True(o.Success, o.Error);
        Assert.Contains("re-sort your load order", o.Note);
    }

    // T36: the link-type note (a linked plugin that could not be read at write time) is kept when no master note joins it.
    [Fact]
    public void ALinkNoteWithNoMasterNoteIsKept()
    {
        var order = _rig.Order(_masterPath, _linkedPath);   // indexed while both open
        var edit = new WritePatchBuilder.PatchEdit
        {
            Target = _weapon, Path = new[] { "Keywords" }, Verb = "Add", Value = _keyword.ToString(),
        };
        WritePatchBuilder.PatchOutcome o;
        using (HeldOpen.Hold(_linkedPath))
            o = WritePatchBuilder.Apply(order, _corpus.Rulebook(), new[] { edit }, _rig.Out("HcWpNoteLink.esp"),
                extend: false, dryRun: true);
        Assert.True(o.Success, o.Error);
        Assert.Contains("HcWpNoteLinked.esm", o.Note);
    }

    public void Dispose() => _rig.Dispose();
}
