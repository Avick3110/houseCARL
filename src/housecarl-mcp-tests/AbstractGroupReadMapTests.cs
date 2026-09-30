using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A query naming an abstract record group's base type unions its concrete arms, on both abstract groups.
/// The fixture's records are made through the product's own abstract-group create. Migrated from the
/// create-abstract-group-guard probe, arm G7.</summary>
[Trait("tier", "integration")]
public sealed class AbstractGroupReadMapTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "hc-absgrp-readmap-" + Guid.NewGuid().ToString("N"));
    readonly LoadOrderService _svc;
    readonly FormKey _globalFloat, _globalInt, _gmstFloat;

    public AbstractGroupReadMapTests()
    {
        var instance = SyntheticInstance.Create(_root);
        var m = new SkyrimMod(new ModKey("HcAbsGrpReadMap", ModType.Master), SkyrimRelease.SkyrimSE);
        _globalFloat = WriteEngine.GenericAddNew(m, "GlobalFloat", "HcRmGlobalFloat").FormKey;
        _globalInt = WriteEngine.GenericAddNew(m, "GlobalInt", "HcRmGlobalInt").FormKey;
        _gmstFloat = WriteEngine.GenericAddNew(m, "GameSettingFloat", "fHcRmGmstFloat").FormKey;
        SyntheticInstance.WriteMod(instance, "ReadMapMod", m);
        var name = m.ModKey.FileName.String;
        SyntheticInstance.WriteProfile(instance, new[] { "# header", "+ReadMapMod" }, new[] { "# header", name }, new[] { "*" + name });
        _svc = LoadOrderService.WithInstance(instance, 0, new UserConfigStore(Path.Combine(_root, "user.json")));
    }

    // G7: type='Global' resolves (no "unknown record type") and unions GlobalFloat+GlobalInt
    [Fact]
    public void TheGlobalBaseNameUnionsBothArms()
    {
        var q = _svc.ReadArea.CrossQuery("Global", null, null, false, null, null, 50);
        Assert.Null(q.Error);
        Assert.Contains(_globalFloat, q.Keys);
        Assert.Contains(_globalInt, q.Keys);
    }

    // G7: type='GameSetting' returns its arm (generality, second group)
    [Fact]
    public void TheGameSettingBaseNameReturnsItsArm()
    {
        var q = _svc.ReadArea.CrossQuery("GameSetting", null, null, false, null, null, 50);
        Assert.Null(q.Error);
        Assert.Contains(_gmstFloat, q.Keys);
    }

    public void Dispose()
    {
        _svc.Dispose();
        try { Directory.Delete(_root, true); } catch { /* temp cleanup best-effort */ }
    }
}
