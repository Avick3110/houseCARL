using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The FormID floor through the report's workflow: a patch born from an override-only <c>Apply</c> persists a counter
/// of at least 0x800, a later create into it allocates 0x800 then 0x801, a remove keeps the high-water, and a legacy
/// patch already on disk with a zeroed counter heals on either lane. <see cref="CreateFormIdFloorTests"/> pins the
/// allocator on a mod in memory; this class drives the patch lanes against files.
/// </summary>
[Trait("tier", "integration")]
public sealed class FormIdFloorWorkflowTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-formid-floor-" + Guid.NewGuid().ToString("N"));
    readonly string _master;
    readonly FormKey _weap;

    public FormIdFloorWorkflowTests()
    {
        Directory.CreateDirectory(_dir);
        var m = new SkyrimMod(new ModKey("HcFidGuardMaster", ModType.Master), SkyrimRelease.SkyrimSE);
        var w = m.Weapons.AddNew(); w.EditorID = "HcFidGuardWeap"; w.BasicStats = new WeaponBasicStats { Damage = 10 };
        _weap = w.FormKey;
        _master = Path.Combine(_dir, m.ModKey.FileName.String);
        m.BeginWrite.ToPath(_master).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).Write();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    // Probe arm CONTROL: Mutagen's default-params serialize of an override-only patch still persists 0 (the seed).
    [Fact]
    public void MutagensDefaultSerializeOfAnOverrideOnlyPatchStillPersistsZero() =>
        Assert.Equal(0u, Counter(LegacyZeroCounterPatch("control")));

    // Probe arm APPLY: a patch born from Apply persists a counter of at least 0x800.
    [Fact]
    public void APatchBornFromApplyPersistsAFlooredCounter()
    {
        var patch = Path.Combine(_dir, "HcFidGuardPatch.esp");
        ApplyDamage(patch, "20", extend: false);

        Assert.True(Counter(patch) >= 0x800, $"counter 0x{Counter(patch):X}");
    }

    // Probe arm CREATE: create into= the Apply-born patch allocates 0x800 then 0x801, and the counter advances.
    [Fact]
    public void CreatesIntoAnApplyBornPatchAllocateFrom0x800()
    {
        var patch = Path.Combine(_dir, "HcFidGuardPatch.esp");
        ApplyDamage(patch, "20", extend: false);

        Assert.Equal(0x800u, CreateKeyword(patch, "HcFidGuardKw1", extend: true).ID);
        Assert.Equal(0x801u, Counter(patch));
        Assert.Equal(0x801u, CreateKeyword(patch, "HcFidGuardKw2", extend: true).ID);
        Assert.Equal(0x802u, Counter(patch));
    }

    // Probe arm REMOVE: removing the second created record keeps the counter at its high-water.
    [Fact]
    public void RemovingTheLastCreatedRecordKeepsTheCounterAtItsHighWater()
    {
        var patch = Path.Combine(_dir, "HcFidGuardPatch.esp");
        ApplyDamage(patch, "20", extend: false);
        CreateKeyword(patch, "HcFidGuardKw1", extend: true);
        var second = CreateKeyword(patch, "HcFidGuardKw2", extend: true);

        using (var r = LoadOrderResolver.Build(new[] { _master }))
        {
            var o = WritePatchBuilder.RemoveRecords(r, new[] { second }, patch);
            Assert.True(o.Success, o.Error);
        }

        Assert.Equal(0x802u, Counter(patch));
    }

    // Probe arm FRESH: a create into a fresh patch allocates 0x800.
    [Fact]
    public void ACreateIntoAFreshPatchAllocates0x800()
    {
        var patch = Path.Combine(_dir, "HcFidGuardFresh.esp");

        Assert.Equal(0x800u, CreateKeyword(patch, "HcFidGuardKwF", extend: false).ID);
        Assert.Equal(0x801u, Counter(patch));
    }

    // Probe arm LEGACY-CREATE: a create into a legacy zero-counter patch allocates 0x800 (the allocator's floor).
    [Fact]
    public void ACreateIntoALegacyZeroCounterPatchAllocates0x800()
    {
        var patch = LegacyZeroCounterPatch("legacy-create");

        Assert.Equal(0x800u, CreateKeyword(patch, "HcFidGuardKwL", extend: true).ID);
        Assert.Equal(0x801u, Counter(patch));
    }

    // Probe arm LEGACY-APPLY: an override-only Apply extend of a legacy zero-counter patch persists 0x800 (the write's floor).
    [Fact]
    public void AnApplyExtendOfALegacyZeroCounterPatchPersistsTheFloor()
    {
        var patch = LegacyZeroCounterPatch("legacy-apply");

        ApplyDamage(patch, "21", extend: true);

        Assert.Equal(0x800u, Counter(patch));
    }

    /// <summary>An override-only patch written with Mutagen's default params: the zeroed counter a pre-fix patch carried.</summary>
    string LegacyZeroCounterPatch(string folder)
    {
        var path = Path.Combine(_dir, folder, "HcFidGuardCtl.esp");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var mOv = SkyrimMod.CreateFromBinaryOverlay(_master, SkyrimRelease.SkyrimSE);
        var ctl = new SkyrimMod(new ModKey("HcFidGuardCtl", ModType.Plugin), SkyrimRelease.SkyrimSE);
        ctl.Weapons.GetOrAddAsOverride(mOv.Weapons.First(x => x.FormKey == _weap)).BasicStats!.Damage = 99;
        ctl.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { mOv }).Write();
        return path;
    }

    void ApplyDamage(string patch, string value, bool extend)
    {
        using var r = LoadOrderResolver.Build(new[] { _master });
        var edit = new WritePatchBuilder.PatchEdit { Target = _weap, Path = new[] { "BasicStats", "Damage" }, Verb = "Set", Value = value };
        var o = WritePatchBuilder.Apply(r, TestCorpus.Rulebook, new[] { edit }, patch, extend);
        Assert.True(o.Success, o.Error);
    }

    FormKey CreateKeyword(string patch, string editorId, bool extend)
    {
        using var r = LoadOrderResolver.Build(new[] { _master });
        var spec = new WritePatchBuilder.CreateSpec { RecordType = "Keyword", EditorId = editorId, Edits = Array.Empty<WriteRequest>() };
        var o = WritePatchBuilder.CreateRecords(r, TestCorpus.Rulebook, new[] { spec }, patch, extend);
        Assert.True(o.Success, o.Error);
        return o.Created[0].FormKey;
    }

    static uint Counter(string path)
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return ov.ModHeader.Stats.NextFormID;
    }
}
