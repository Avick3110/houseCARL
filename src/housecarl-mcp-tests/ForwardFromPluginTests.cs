using System.Security.Cryptography;
using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// Forwarding a NAMED plugin's version of a record: an earlier override beats the winner with only the origin master
/// in the header, a nested record takes the same route, a re-forward replaces and grows the header, an edit after a
/// forward lands on the forwarded copy, the sources are never written, the doesn't-define and duplicate refusals, and
/// the in-place route's consent, replace and contract. Migrated from the forward-from-plugin-guard probe; its arms that
/// <see cref="WritePatchForwardTests"/> and <see cref="WriteSurfaceForwardTests"/> already make whole are not repeated here.
///
/// Fixture, priority master -> ModA -> ModB -> Other: master defines weapons X (Damage 10) and Y (100) and a placed
/// object P (Scale 1); ModA overrides X 20, Y 200, P 2; ModB overrides X 30 (carrying a keyword ModB defines), Y 300,
/// P 3; Other defines only its own weapon. The read-back value says which plugin's version landed.
/// </summary>
[Trait("tier", "integration")]
public sealed class ForwardFromPluginTests : IDisposable
{
    const string MasterName = "HcFwdMaster.esm";
    const string ModAName = "HcFwdModA.esp";
    const string ModBName = "HcFwdModB.esp";
    const string OtherName = "HcFwdOther.esp";

    readonly WritePathRig _rig = new();
    readonly string _mPath, _aPath, _bPath, _oPath;
    readonly FormKey _x, _y, _placed;

    public ForwardFromPluginTests()
    {
        var m = new SkyrimMod(ModKey.FromFileName(MasterName), SkyrimRelease.SkyrimSE);
        var x = m.Weapons.AddNew(); x.EditorID = "HcFwdWeapX"; x.BasicStats = new WeaponBasicStats { Damage = 10 };
        var y = m.Weapons.AddNew(); y.EditorID = "HcFwdWeapY"; y.BasicStats = new WeaponBasicStats { Damage = 100 };
        var cell = new Cell(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcFwdCell" };
        var placed = new PlacedObject(m.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcFwdRef", Scale = 1.0f };
        cell.Persistent.Add(placed);
        var sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock };
        sub.Cells.Add(cell);
        var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock };
        block.SubBlocks.Add(sub);
        m.Cells.Records.Add(block);
        _x = x.FormKey; _y = y.FormKey; _placed = placed.FormKey;
        _mPath = _rig.Write(m);
        var cache = m.ToImmutableLinkCache();

        var a = new SkyrimMod(ModKey.FromFileName(ModAName), SkyrimRelease.SkyrimSE);
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(a, x)).BasicStats!.Damage = 20;
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(a, y)).BasicStats!.Damage = 200;
        ((IPlacedObject)WriteEngine.GenericGetOrAddAsOverride(a, placed, cache)).Scale = 2.0f;
        _aPath = _rig.Write(a, "plugins", m);

        var b = new SkyrimMod(ModKey.FromFileName(ModBName), SkyrimRelease.SkyrimSE);
        var bx = (IWeapon)WriteEngine.GenericGetOrAddAsOverride(b, x);
        bx.BasicStats!.Damage = 30;
        var bkw = b.Keywords.AddNew(); bkw.EditorID = "HcFwdModBKw";
        (bx.Keywords ??= new()).Add(bkw.FormKey.ToLink<IKeywordGetter>());
        ((IWeapon)WriteEngine.GenericGetOrAddAsOverride(b, y)).BasicStats!.Damage = 300;
        ((IPlacedObject)WriteEngine.GenericGetOrAddAsOverride(b, placed, cache)).Scale = 3.0f;
        _bPath = _rig.Write(b, "plugins", m);

        var o = new SkyrimMod(ModKey.FromFileName(OtherName), SkyrimRelease.SkyrimSE);
        var w = o.Weapons.AddNew(); w.EditorID = "HcFwdWeapW"; w.BasicStats = new WeaponBasicStats { Damage = 5 };
        _oPath = _rig.Write(o);
    }

    LoadOrderResolver Order() => _rig.Order(_mPath, _aPath, _bPath, _oPath);

    static WritePatchBuilder.ForwardSpec Spec(FormKey target, string from) => new() { Target = target, FromPlugin = from };

    WritePatchBuilder.ForwardOutcome Forward(string path, bool extend, params WritePatchBuilder.ForwardSpec[] specs)
        => WritePatchBuilder.ForwardRecords(Order(), specs, path, extend, sourceParam: "source=");

    ushort? Damage(string path, FormKey fk) =>
        _rig.Open(path).EnumerateMajorRecords<IWeaponGetter>().FirstOrDefault(r => r.FormKey == fk)?.BasicStats?.Damage;

    LoadOrderService Service() => LoadOrderService.ForGuard(Order(), new UserConfigStore(Path.Combine(_rig.Root, "user.json")));

    string XId => $"{_x.ID:X6}:{MasterName}";

    // SETUP: winner of X resolves to ModB (the override the forward must beat)
    // FORWARD-NONWINNER: ModA's X (Dmg 20) copied, NOT winner ModB's (30)
    [Fact]
    public void AnEarlierOverrideIsCopiedOverTheWinner()
    {
        Assert.Equal(ModBName, Order().Capture().ResolveWinner(_x)?.WinnerPlugin);
        var path = _rig.Out("HcFwdNonWinner.esp");
        var o = Forward(path, false, Spec(_x, ModAName));
        Assert.True(o.Success, o.Error);
        Assert.Equal((ushort)20, Damage(path, _x));
    }

    // FORWARD-NONWINNER: header=[origin master only] (content is copied, not mastered)
    [Fact]
    public void TheHeaderCarriesOnlyTheOriginMaster()
    {
        var o = Forward(_rig.Out("HcFwdNonWinner.esp"), false, Spec(_x, ModAName));
        Assert.True(o.Success, o.Error);
        Assert.Equal(new[] { MasterName }, o.Masters, StringComparer.OrdinalIgnoreCase);
    }

    // FORWARD-NONWINNER: winner reported (prior winner ModB, not already the winner)
    [Fact]
    public void ThePriorWinnerIsReported()
    {
        var o = Forward(_rig.Out("HcFwdNonWinner.esp"), false, Spec(_x, ModAName));
        var f = Assert.Single(o.Forwarded);
        Assert.Equal(ModBName, f.PriorWinner);
        Assert.False(f.WasAlreadyWinner);
    }

    // NESTED: forwarding a PlacedObject (nested link-cache path) copies ModA's version (Scale 2.0), NOT the winner's (3.0)
    [Fact]
    public void ANestedRecordForwardsTheNamedPluginsVersion()
    {
        var path = _rig.Out("HcFwdNested.esp");
        var o = Forward(path, false, Spec(_placed, ModAName));
        Assert.True(o.Success, o.Error);
        Assert.Equal(2.0f, _rig.Open(path).EnumerateMajorRecords<IPlacedObjectGetter>().Single(r => r.FormKey == _placed).Scale);
    }

    // MULTI: two records forwarded from ModA in one call both land (X=20, Y=200)
    [Fact]
    public void TwoRecordsInOneCallBothLand()
    {
        var path = _rig.Out("HcFwdMulti.esp");
        var o = Forward(path, false, Spec(_x, ModAName), Spec(_y, ModAName));
        Assert.True(o.Success, o.Error);
        Assert.Equal((ushort)20, Damage(path, _x));
        Assert.Equal((ushort)200, Damage(path, _y));
    }

    // EXTEND: forward X fresh, then Y into the same patch (into=) — both survive (X=20, Y=200)
    [Fact]
    public void AForwardIntoTheSamePatchKeepsTheEarlierOne()
    {
        var path = _rig.Out("HcFwdExtend.esp");
        Assert.True(Forward(path, false, Spec(_x, ModAName)).Success);
        var second = Forward(path, true, Spec(_y, ModAName));
        Assert.True(second.Success, second.Error);
        Assert.True(second.Extended);
        Assert.Equal((ushort)20, Damage(path, _x));
        Assert.Equal((ushort)200, Damage(path, _y));
    }

    // REPLACE: re-forwarding a FormKey the patch already carries ... grows masters (+ModB)
    [Fact]
    public void AReForwardGrowsTheHeaderFromTheNewBody()
    {
        var path = _rig.Out("HcFwdReplace.esp");
        Assert.True(Forward(path, false, Spec(_x, ModAName)).Success);
        var o = Forward(path, true, Spec(_x, ModBName));
        Assert.True(o.Success, o.Error);
        Assert.Equal((ushort)30, Damage(path, _x));
        Assert.Contains(ModBName, o.Masters, StringComparer.OrdinalIgnoreCase);
    }

    // FORWARD-THEN-EDIT: bulk_apply into= edits the patch's FORWARDED copy (Dmg stays 20 + Weight lands 7) — never re-resolves the stale winner (30)
    [Fact]
    public void AnEditAfterAForwardLandsOnTheForwardedCopy()
    {
        var path = _rig.Out("HcFwdThenEdit.esp");
        Assert.True(Forward(path, false, Spec(_x, ModAName)).Success);
        var edit = new WritePatchBuilder.PatchEdit { Target = _x, Path = new[] { "BasicStats", "Weight" }, Verb = "Set", Value = "7" };
        var o = WritePatchBuilder.Apply(Order(), TestCorpus.Rulebook, new[] { edit }, path, extend: true);
        Assert.True(o.Success, o.Error);
        var w = _rig.Open(path).EnumerateMajorRecords<IWeaponGetter>().Single(r => r.FormKey == _x);
        Assert.Equal((ushort)20, w.BasicStats!.Damage);
        Assert.Equal(7f, w.BasicStats.Weight);
    }

    static string Sha(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    // ORIGINALS: the 4 source plugins are byte-identical after the run (only the patch is written)
    [Fact]
    public void TheSourcePluginsAreNeverWritten()
    {
        var sources = new[] { _mPath, _aPath, _bPath, _oPath };
        var before = sources.Select(Sha).ToList();
        var path = _rig.Out("HcFwdOriginals.esp");
        Assert.True(Forward(path, false, Spec(_x, ModAName), Spec(_placed, ModAName)).Success);
        Assert.True(Forward(path, true, Spec(_x, ModBName)).Success);
        Assert.Equal(before, sources.Select(Sha));
    }

    // REJ-DOESNTDEFINE: source in the order but doesn't define the record refuses loud, no file
    [Fact]
    public void ASourceThatDoesNotDefineTheRecordIsRefused()
    {
        var path = _rig.Out("HcFwdNoDef.esp");
        var o = Forward(path, false, Spec(_x, OtherName));
        Assert.False(o.Success);
        Assert.Contains("does NOT define", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));
    }

    // ALREADY-WINNER: forwarding the current winner succeeds (Dmg 30) and is flagged redundant (Q3, not silent)
    [Fact]
    public void ForwardingTheWinnerWritesTheWinnersBodyAndFlagsIt()
    {
        var path = _rig.Out("HcFwdAlreadyWin.esp");
        var o = Forward(path, false, Spec(_x, ModBName));
        Assert.True(o.Success, o.Error);
        Assert.True(Assert.Single(o.Forwarded).WasAlreadyWinner);
        Assert.Equal((ushort)30, Damage(path, _x));
    }

    // REJ-NOTINORDER: source plugin not in the order refuses loud, no file
    [Fact]
    public void ASourceNotInTheOrderIsRefusedAndWritesNothing()
    {
        var path = _rig.Out("HcFwdNotInOrder.esp");
        var o = Forward(path, false, Spec(_x, "NotReal.esp"));
        Assert.False(o.Success);
        Assert.Contains("not in the load order", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));
    }

    // REJ-INTOSELF: from_plugin == the output patch itself refuses loud, no file
    [Fact]
    public void ASourceThatIsTheOutputPatchIsRefusedAndWritesNothing()
    {
        var path = _rig.Out("HcFwdSelf.esp");
        var o = Forward(path, false, Spec(_x, "HcFwdSelf.esp"));
        Assert.False(o.Success);
        Assert.Contains("output patch itself", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));
    }

    // REJ-DUP: the same target twice in one call refuses loud, no file
    [Fact]
    public void TheSameTargetTwiceIsRefused()
    {
        var path = _rig.Out("HcFwdDup.esp");
        var o = Forward(path, false, Spec(_x, ModAName), Spec(_x, ModBName));
        Assert.False(o.Success);
        Assert.Contains("more than once", o.Error);
        Assert.False(File.Exists(path));
    }

    // INPLACE-NEW: consent RED (untouched)
    [Fact]
    public void AnUnacknowledgedInPlaceForwardAsksAndLeavesTheFile()
    {
        var before = File.ReadAllBytes(_oPath);
        var o = Service().ForwardRecords(new[] { XId }, ModAName, null, null, target: OtherName, inPlace: true, acknowledge: false);
        Assert.False(o.Success);
        Assert.True(o.NeedsAcknowledge);
        Assert.Equal(before, File.ReadAllBytes(_oPath));
    }

    // INPLACE-NEW: -> GREEN; ModA's X lands in Other's own file (20) + origin master joins its header + re-sort note
    [Fact]
    public void AnAcknowledgedInPlaceForwardLandsInTheTargetAndNotesTheNewMaster()
    {
        var o = Service().ForwardRecords(new[] { XId }, ModAName, null, null, target: OtherName, inPlace: true, acknowledge: true);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        Assert.False(Assert.Single(o.Forwarded).ReplacedExisting);
        Assert.Contains(MasterName, o.Masters, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("added as a master", o.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((ushort)20, Damage(_oPath, _x));
    }

    // INPLACE-REPLACE: a FormKey the target carries is replaced in its own file (X=30, not 20), flagged, masters grow (+ModB) + re-sort note
    [Fact]
    public void AnInPlaceForwardReplacesTheTargetsOwnCopy()
    {
        var o = Service().ForwardRecords(new[] { XId }, ModBName, null, null, target: ModAName, inPlace: true, acknowledge: true);
        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        Assert.True(Assert.Single(o.Forwarded).ReplacedExisting);
        Assert.Contains(ModBName, o.Masters, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("added as a master", o.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((ushort)30, Damage(_aPath, _x));
    }

    // INPLACE-CONTRACT: in_place<=>target, exclusive of into= (named refusals)
    [Fact]
    public void TheInPlaceContractIsRefusedByNameOnTheForwardLane()
    {
        var svc = Service();
        Assert.Contains("requires target=", svc.ForwardRecords(new[] { XId }, ModAName, null, null, false, target: null, inPlace: true, acknowledge: true).Error);
        Assert.Contains("mutually exclusive", svc.ForwardRecords(new[] { XId }, ModAName, null, "somepatch", false, target: OtherName, inPlace: true, acknowledge: true).Error);
        Assert.Contains("only meaningful with in_place", svc.ForwardRecords(new[] { XId }, ModAName, null, null, false, target: OtherName, inPlace: false).Error);
    }

    // INPLACE-OPTIN: forward's in_place/acknowledge default OFF, and it declares no target=
    [Fact]
    public void TheForwardToolsInPlaceIsOffByDefaultAndDeclaresNoTarget()
    {
        var ps = typeof(ForwardTools).GetMethod(nameof(ForwardTools.Forward))!.GetParameters();
        Assert.Null(ps.First(p => p.Name == "in_place").DefaultValue);
        Assert.Equal(false, ps.First(p => p.Name == "acknowledge").DefaultValue);
        Assert.DoesNotContain(ps, p => p.Name == "target");
    }

    public void Dispose() => _rig.Dispose();
}
