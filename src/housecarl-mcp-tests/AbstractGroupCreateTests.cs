using HousecarlCore;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A concrete arm of an abstract record group (Global, GameSetting) creates, edits, round-trips and upserts
/// through the create lane; the bare abstract base is refused naming its arms. Migrated from the
/// create-abstract-group-guard probe, arms G1 to G6.</summary>
[Trait("tier", "integration")]
public sealed class AbstractGroupCreateTests : IDisposable
{
    const string GlobalEdid = "HcAbsGrpGlobal";
    const string GmstEdid = "fHcAbsGrpGmst";

    readonly WritePathRig _rig = new();
    readonly string _masterPath;

    public AbstractGroupCreateTests()
    {
        var m = new SkyrimMod(new ModKey("HcAbsGrpMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        m.Keywords.AddNew().EditorID = "HcAbsGrpMasterKw";
        _masterPath = _rig.Write(m);
    }

    static WritePatchBuilder.CreateSpec GlobalFloat(string data) => new()
    {
        RecordType = "GlobalFloat", EditorId = GlobalEdid,
        Edits = new[] { new WriteRequest { RecordType = "GlobalFloat", Path = new[] { "Data" }, Verb = "Set", Value = data } },
    };

    static WritePatchBuilder.CreateSpec GameSettingFloat() =>
        new() { RecordType = "GameSettingFloat", EditorId = GmstEdid, Edits = Array.Empty<WriteRequest>() };

    WritePatchBuilder.CreateOutcome Create(string path, bool extend, params WritePatchBuilder.CreateSpec[] specs)
        => WritePatchBuilder.CreateRecords(_rig.Order(_masterPath), TestCorpus.Rulebook, specs, path, extend);

    // G1: create GlobalFloat (arm of abstract Global): a GlobalFloat, FormKey id >= 0x800, local to the patch
    [Fact]
    public void AGlobalFloatCreatesAsALocalRecord()
    {
        var path = _rig.Out("HcAbsGrpGuard.esp");
        var o = Create(path, false, GlobalFloat("12.5"), GameSettingFloat());
        Assert.True(o.Success, o.Error);
        var fk = o.Created.Single(c => c.EditorId == GlobalEdid).FormKey;
        var ov = _rig.Open(path);
        Assert.IsAssignableFrom<IGlobalFloatGetter>(ov.Globals.Single(g => g.FormKey == fk));
        Assert.True(fk.ID >= 0x800);
        Assert.Equal(ov.ModKey, fk.ModKey);
    }

    // G2: create GameSettingFloat (generality, NOT GLOB): the same off the same branch
    [Fact]
    public void AGameSettingFloatCreatesAsALocalRecord()
    {
        var path = _rig.Out("HcAbsGrpGuard.esp");
        var o = Create(path, false, GlobalFloat("12.5"), GameSettingFloat());
        Assert.True(o.Success, o.Error);
        var fk = o.Created.Single(c => c.EditorId == GmstEdid).FormKey;
        var ov = _rig.Open(path);
        Assert.IsAssignableFrom<IGameSettingFloatGetter>(ov.GameSettings.Single(g => g.FormKey == fk));
        Assert.True(fk.ID >= 0x800);
        Assert.Equal(ov.ModKey, fk.ModKey);
    }

    // G3: set Data=12.5 via ApplyVerb, read back
    // G4: serialize + re-read round-trip (FormKey/edid)
    [Fact]
    public void TheCreatedGlobalCarriesItsEditAndEditorIdOnDisk()
    {
        var path = _rig.Out("HcAbsGrpGuard.esp");
        var o = Create(path, false, GlobalFloat("12.5"), GameSettingFloat());
        Assert.True(o.Success, o.Error);
        var fk = o.Created.Single(c => c.EditorId == GlobalEdid).FormKey;
        var g = Assert.IsAssignableFrom<IGlobalFloatGetter>(_rig.Open(path).Globals.Single(x => x.FormKey == fk));
        Assert.Equal(GlobalEdid, g.EditorID);
        Assert.Equal(12.5f, g.Data);
    }

    // G5: re-run replaces in place (1 copy, stable FormKey, REPLACED flagged)
    [Fact]
    public void ReRunningTheCreateWithExtendReplacesTheArmInPlace()
    {
        var path = _rig.Out("HcAbsGrpGuard.esp");
        var first = Create(path, false, GlobalFloat("12.5"), GameSettingFloat());
        Assert.True(first.Success, first.Error);
        var fk = first.Created.Single(c => c.EditorId == GlobalEdid).FormKey;

        var again = Create(path, true, GlobalFloat("99.0"));
        Assert.True(again.Success, again.Error);
        var created = again.Created.Single(c => c.EditorId == GlobalEdid);
        Assert.True(created.ReplacedExisting);
        Assert.Equal(fk, created.FormKey);
        var g = Assert.IsAssignableFrom<IGlobalFloatGetter>(Assert.Single(_rig.Open(path).Globals, x => x.EditorID == GlobalEdid));
        Assert.Equal(99f, g.Data);
    }

    // G6: bare abstract base 'Global' refused, arms named, no file
    [Fact]
    public void TheBareAbstractBaseIsRefusedNamingEveryArm()
    {
        var path = _rig.Out("HcAbsGrpBaseRefuse.esp");
        var o = Create(path, false, new WritePatchBuilder.CreateSpec { RecordType = "Global", EditorId = "HcAbsGrpBase", Edits = Array.Empty<WriteRequest>() });
        Assert.False(o.Success);
        Assert.Contains("GlobalFloat", o.Error);
        Assert.Contains("GlobalInt", o.Error);
        Assert.Contains("GlobalShort", o.Error);
        Assert.False(File.Exists(path));
    }

    public void Dispose() => _rig.Dispose();
}
