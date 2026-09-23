using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The apply lane, patch and in-place: an extend edit of a record the patch itself defines, the order a refusal lists
/// its problems in, the json `applied` flag, a wrong-type link refused in place, and what a landed in-place apply
/// reports. Stryker rows T30, T31, T33, T37 and T38 (dev/plans/STRYKER_WRITE_PATH_2026-09-23.md).
/// </summary>
[Trait("tier", "integration")]
public sealed class WritePatchApplyTests : IClassFixture<WritePathCorpus>, IDisposable
{
    const string TargetName = "HcWpApplyTarget.esp";

    readonly WritePathCorpus _corpus;
    readonly WritePathRig _rig = new();
    readonly string _masterPath, _targetPath;
    readonly FormKey _weapon, _keyword, _ownWeapon;

    public WritePatchApplyTests(WritePathCorpus corpus)
    {
        _corpus = corpus;
        var master = new SkyrimMod(new ModKey("HcWpApplyMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var w = master.Weapons.AddNew();
        w.EditorID = "HcWpApplySword";
        w.BasicStats = new WeaponBasicStats { Damage = 10, Weight = 1 };
        _weapon = w.FormKey;
        var k = master.Keywords.AddNew();
        k.EditorID = "HcWpApplyKeyword";
        _keyword = k.FormKey;
        _masterPath = _rig.Write(master);

        // A plugin the in-place lane edits: one weapon of its own.
        var target = new SkyrimMod(ModKey.FromFileName(TargetName), SkyrimRelease.SkyrimSE);
        var own = target.Weapons.AddNew();
        own.EditorID = "HcWpApplyOwnSword";
        own.BasicStats = new WeaponBasicStats { Damage = 3, Weight = 1 };
        _ownWeapon = own.FormKey;
        _targetPath = _rig.Write(target, "target", master);
    }

    LoadOrderResolver Order() => _rig.Order(_masterPath, _targetPath);

    static readonly FormKey NotInOrder = new(ModKey.FromFileName("HcWpApplyNowhere.esp"), 0x800);

    // T30: a record the extended patch itself defines is not in the load order, and still takes an edit.
    [Fact]
    public void AnExtendEditOfARecordThePatchDefinesLands()
    {
        var order = Order();
        var path = _rig.Out("HcWpApplyExtend.esp");
        var created = WritePatchBuilder.CreateRecords(order, _corpus.Rulebook(), new[]
        {
            new WritePatchBuilder.CreateSpec { RecordType = "Weapon", EditorId = "HcWpApplyNewSword", Edits = Array.Empty<WriteRequest>() },
        }, path, extend: false);
        Assert.True(created.Success, created.Error);
        var fk = created.Created[0].FormKey;

        var o = WritePatchBuilder.Apply(order, _corpus.Rulebook(), new[] { WritePathRig.Set(fk, "BasicStats.Damage", "77") }, path, extend: true);

        Assert.True(o.Success, o.Error);
        Assert.Equal(77, _rig.Open(path).Weapons.Single(x => x.FormKey == fk).BasicStats!.Damage);
    }

    // T31: a pre-flight reject at edit 0 and a resolve miss at edit 1 are listed in the caller's edit order.
    [Fact]
    public void APatchRefusalListsItsProblemsInEditOrder()
    {
        var o = WritePatchBuilder.Apply(Order(), _corpus.Rulebook(), new[]
        {
            WritePathRig.Set(_weapon, "NoSuchField", "1"),
            WritePathRig.Set(NotInOrder, "BasicStats.Damage", "1"),
        }, _rig.Out("HcWpApplyOrder.esp"), extend: false);

        Assert.False(o.Success);
        AssertBefore(o.Error!, "NoSuchField", "HcWpApplyNowhere.esp");
    }

    [Fact]
    public void AnInPlaceRefusalListsItsProblemsInEditOrder()
    {
        var o = WritePatchBuilder.ApplyInPlace(Order(), _corpus.Rulebook(), new[]
        {
            WritePathRig.Set(_ownWeapon, "NoSuchField", "1"),
            WritePathRig.Set(_weapon, "BasicStats.Damage", "1"),        // a master record the target does not carry
        }, _targetPath, TargetName);

        Assert.False(o.Success);
        AssertBefore(o.Error!, "NoSuchField", FormIdToken.Of(_weapon));
    }

    static void AssertBefore(string text, string first, string second)
    {
        int a = text.IndexOf(first, StringComparison.Ordinal), b = text.IndexOf(second, StringComparison.Ordinal);
        Assert.True(a >= 0 && b >= 0 && a < b, $"expected '{first}' before '{second}' in: {text}");
    }

    // T33: every op a write applied says so on the json wire.
    [Fact]
    public void TheJsonApplyResultMarksEveryOpApplied()
    {
        var o = WritePatchBuilder.Apply(Order(), _corpus.Rulebook(), new[]
        {
            WritePathRig.Set(_weapon, "BasicStats.Damage", "42"),
            WritePathRig.Set(_weapon, "BasicStats.Weight", "2"),
        }, _rig.Out("HcWpApplyJson.esp"), extend: false);
        Assert.True(o.Success, o.Error);

        AssertAllApplied(JsonDocument.Parse(JsonWire.RenderPatchOutcome(o, 0, false, "patch")).RootElement.GetProperty("ops"), 2);
    }

    [Fact]
    public void TheJsonInPlaceApplyResultMarksEveryOpApplied()
    {
        var o = WritePatchBuilder.ApplyInPlace(Order(), _corpus.Rulebook(),
            new[] { WritePathRig.Set(_ownWeapon, "BasicStats.Damage", "42") }, _targetPath, TargetName);
        Assert.True(o.Success, o.Error);

        AssertAllApplied(JsonDocument.Parse(JsonWire.RenderPatchOutcome(o, 0, false, "in_place")).RootElement.GetProperty("ops"), 1);
    }

    /// <summary>A quest gets CK-parity fill ops beside the one it asked for; those are applied ops too.</summary>
    [Fact]
    public void TheJsonCreateResultMarksEveryOpAppliedFillsIncluded()
    {
        var o = WritePatchBuilder.CreateRecords(Order(), _corpus.Rulebook(), new[]
        {
            new WritePatchBuilder.CreateSpec
            {
                RecordType = "Quest", EditorId = "HcWpApplyQuest",
                Edits = new[] { WritePathRig.Req("Quest", "Priority", "Set", "5") },
            },
        }, _rig.Out("HcWpApplyCreateJson.esp"), extend: false);
        Assert.True(o.Success, o.Error);
        Assert.Contains(o.Created[0].Ops, op => op.AfterIsNote);   // a fill is among them

        var created = JsonDocument.Parse(JsonWire.RenderCreateOutcome(o, 0, false, "patch")).RootElement.GetProperty("created")[0];
        AssertAllApplied(created.GetProperty("ops"), o.Created[0].Ops.Count);
    }

    static void AssertAllApplied(JsonElement ops, int count)
    {
        Assert.Equal(count, ops.GetArrayLength());
        foreach (var op in ops.EnumerateArray()) Assert.True(op.GetProperty("applied").GetBoolean(), op.ToString());
    }

    // T37: in place, a link set to a record of the wrong type is refused by the link-target pre-flight.
    [Fact]
    public void AnInPlaceLinkToAWrongTypeRecordIsRefused()
    {
        var o = WritePatchBuilder.ApplyInPlace(Order(), _corpus.Rulebook(),
            new[] { WritePathRig.Set(_ownWeapon, "EquipmentType", _keyword.ToString()) }, _targetPath, TargetName);

        Assert.False(o.Success);
        Assert.Contains("EquipType", o.Error);   // what the field links to
    }

    // T38: a landed in-place apply succeeds and did not extend a patch.
    [Fact]
    public void ALandedInPlaceApplyReportsSuccessNotAnExtend()
    {
        var o = WritePatchBuilder.ApplyInPlace(Order(), _corpus.Rulebook(),
            new[] { WritePathRig.Set(_ownWeapon, "BasicStats.Damage", "42") }, _targetPath, TargetName);

        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        Assert.False(o.Extended);
    }

    public void Dispose() => _rig.Dispose();
}
