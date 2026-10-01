using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A plugin= read naming a plugin outside the order says so and points at readback, never "does not define";
/// an in-order plugin that does not touch the record keeps its own message; and a write's opt-in full read-back hands
/// back every touched or created record whole, re-read from the written file. Migrated from the verify-loop-guard
/// probe.</summary>
[Trait("tier", "integration")]
public sealed class VerifyLoopTests : IDisposable
{
    const string MasterName = "hcVerifyMaster.esp", OvrName = "hcVerifyOvr.esp", GhostName = "hcVerifyGhost.esp";

    readonly WritePathRig _rig = new();
    readonly LoadOrderResolver _order;
    readonly LoadOrderService _svc;
    readonly FormKey _w1, _w2, _perk, _cobj;

    public VerifyLoopTests()
    {
        var master = new SkyrimMod(ModKey.FromFileName(MasterName), SkyrimRelease.SkyrimSE);
        var w1 = master.Weapons.AddNew(); w1.EditorID = "hcVerifyW1"; w1.Name = "Verify Weapon One";
        w1.BasicStats = new WeaponBasicStats { Damage = 10 };
        var w2 = master.Weapons.AddNew(); w2.EditorID = "hcVerifyW2";
        var perk = master.Perks.AddNew(); perk.EditorID = "hcVerifyGatePerk";
        var cobj = master.ConstructibleObjects.AddNew(); cobj.EditorID = "hcVerifyRecipe";
        (_w1, _w2, _perk, _cobj) = (w1.FormKey, w2.FormKey, perk.FormKey, cobj.FormKey);
        var masterPath = _rig.Write(master);

        var ovr = new SkyrimMod(ModKey.FromFileName(OvrName), SkyrimRelease.SkyrimSE);
        WriteEngine.GenericGetOrAddAsOverride(ovr, w1);   // touches W1 only
        var ovrPath = _rig.Write(ovr, "plugins", master);

        _order = _rig.Order(masterPath, ovrPath);
        _svc = LoadOrderService.ForGuard(_order, new UserConfigStore(Path.Combine(_rig.Root, "houseCARL.user.json")));
    }

    public void Dispose() => _rig.Dispose();

    // TAXONOMY MISS: plugin= naming a not-in-order plugin FAILS, names 'not in the load order', never 'does not define',
    // and points at readback
    [Fact]
    public void APluginOutsideTheOrderIsNamedAsSuchAndPointsAtReadback()
    {
        var miss = _svc.ReadArea.ResolveRead(_w1, GhostName, null, conflictTree: false);
        Assert.NotNull(miss.Error);
        Assert.Contains("not in the load order", miss.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("does not define", miss.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("readback", miss.Error, StringComparison.OrdinalIgnoreCase);
    }

    // TAXONOMY CONTROL: an IN-ORDER plugin that doesn't define the record keeps the true does-not-touch message, touchers named
    [Fact]
    public void AnInOrderPluginThatDoesNotTouchTheRecordSaysSoAndNamesTheTouchers()
    {
        var r = _svc.ReadArea.ResolveRead(_w2, OvrName, null, conflictTree: false);
        Assert.Contains("does not touch", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Touched by", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MasterName, r.Error, StringComparison.OrdinalIgnoreCase);
    }

    // TAXONOMY CONTROL: an in-order plugin that DOES define the record still reads clean (source = the named plugin)
    [Fact]
    public void AnInOrderPluginThatTouchesTheRecordReadsFromThatPlugin()
    {
        var r = _svc.ReadArea.ResolveRead(_w1, OvrName, null, conflictTree: false);
        Assert.Null(r.Error);
        Assert.Equal(OvrName, r.SourcePlugin, ignoreCase: true);
    }

    WritePatchBuilder.PatchOutcome ApplyWithReadback(string patchPath) =>
        WritePatchBuilder.Apply(_order, TestCorpus.Rulebook, new WritePatchBuilder.PatchEdit[]
        {
            new() { Target = _w1, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = "777" },
            new() { Target = _cobj, Path = new[] { "Conditions" }, Verb = "Add",
                    Struct = new StructSpec
                    {
                        Type = "ConditionFloat",
                        Fields = new Dictionary<string, string> { ["CompareOperator"] = "EqualTo", ["ComparisonValue"] = "1" },
                    } },
            new() { Target = _cobj, Path = new[] { "Conditions[0]", "Data" }, Verb = "Set",
                    Struct = new StructSpec
                    {
                        Type = "HasPerkConditionData",
                        Sets = new List<WriteRequest>
                        {
                            new() { RecordType = "HasPerkConditionData", Path = new[] { "Perk" }, Verb = "Set", Value = _perk.ToString() },
                        },
                    } },
        }, patchPath, extend: false, fullReadback: true);

    // READBACK: ReadBack present with BOTH touched records (3 ops -> 2 records); the edited leaf shows the new value;
    // a field the call never touched is still there (the whole record)
    [Fact]
    public void FullReadbackReturnsEachTouchedRecordWholeFromTheWrittenFile()
    {
        var o = ApplyWithReadback(_rig.Out("hcVerifyPatch.esp"));
        Assert.True(o.Success, o.Error);
        Assert.NotNull(o.ReadBack);
        Assert.Equal(2, o.ReadBack!.Count);
        Assert.All(o.ReadBack, r => { Assert.Null(r.Error); Assert.NotNull(r.Record); });

        var w1 = o.ReadBack.Single(r => r.Record!.FormKey == _w1.ToString()).Record!;
        Assert.Contains(w1.Fields, f => f.Path.Contains("Damage", StringComparison.OrdinalIgnoreCase) && f.HasValue && f.Token == "777");
        Assert.Contains(w1.Fields, f => f.Path == "Name" && f.HasValue && (f.Token ?? "").Contains("Verify Weapon One"));
    }

    // READBACK: the written perk gate is verifiable PRE-ENABLE — the COBJ read-back surfaces Conditions content
    // referencing the gate perk; GROUND TRUTH: the written file carries the HasPerk gate on the COBJ
    [Fact]
    public void FullReadbackShowsTheComposedPerkGateAndTheFileCarriesIt()
    {
        var path = _rig.Out("hcVerifyPatch.esp");
        var o = ApplyWithReadback(path);
        Assert.True(o.Success, o.Error);

        var cobj = o.ReadBack!.Single(r => r.Record!.FormKey == _cobj.ToString()).Record!;
        Assert.Contains(cobj.Fields, f => f.Path.StartsWith("Conditions", StringComparison.OrdinalIgnoreCase)
            && ((f.Token ?? "") + (f.Note ?? "")).Contains(_perk.ToString(), StringComparison.OrdinalIgnoreCase));

        var written = _rig.Open(path).ConstructibleObjects.Single(x => x.FormKey == _cobj);
        var gate = Assert.IsAssignableFrom<IHasPerkConditionDataGetter>(Assert.Single(written.Conditions).Data);
        Assert.Equal(_perk, WriteEngine.ReadFloiFormKey(gate.Perk));
    }

    // READBACK CONTROL: without fullReadback the outcome carries NO ReadBack
    [Fact]
    public void WithoutFullReadbackNoReadbackIsReturned()
    {
        var o = WritePatchBuilder.Apply(_order, TestCorpus.Rulebook,
            new[] { WritePathRig.Set(_w1, "BasicStats.Damage", "778") }, _rig.Out("hcVerifySilent.esp"), extend: false);
        Assert.True(o.Success, o.Error);
        Assert.Null(o.ReadBack);
    }

    // READBACK CREATE: CreateRecords with fullReadback hands back the new record in full (editorid HcVerifyKw)
    [Fact]
    public void ACreateWithFullReadbackReturnsTheNewRecord()
    {
        var o = WritePatchBuilder.CreateRecords(_order, TestCorpus.Rulebook,
            new[] { new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = "HcVerifyKw", Edits = Array.Empty<WriteRequest>() } },
            _rig.Out("hcVerifyCreate.esp"), extend: false, fullReadback: true);
        Assert.True(o.Success, o.Error);
        var rb = Assert.Single(o.ReadBack!);
        Assert.Null(rb.Error);
        Assert.Equal("HcVerifyKw", rb.Record!.EditorId);
    }
}
