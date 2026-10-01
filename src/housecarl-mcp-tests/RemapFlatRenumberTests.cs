using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>RemapEngine</c>'s flat compact, end to end over files on disk: HcRemapDonor.esp (a weapon at 0xAAA and a
/// FormList at 0xCCC naming it) renumbered into the light window, and HcRemapExternal.esp (a FormList naming the
/// donor's weapon) found by the identify pass and repointed in place. Plus the loud refusals: window overflow, a
/// nested-only record, and RepointInPlace's bad input. Each test gets its own temp folder.</summary>
[Trait("tier", "integration")]
public sealed class RemapFlatRenumberTests : IDisposable
{
    static readonly ModKey DonorKey = new("HcRemapDonor", ModType.Plugin);
    static readonly ModKey ExtKey = new("HcRemapExternal", ModType.Plugin);
    static readonly FormKey WaOld = new(DonorKey, 0xAAA);
    static readonly FormKey FlOld = new(DonorKey, 0xCCC);

    readonly string _dir = Path.Combine(Path.GetTempPath(), "hc-remap-flat-" + Guid.NewGuid().ToString("N"));
    readonly string _donorPath;
    readonly string _extPath;

    public RemapFlatRenumberTests()
    {
        Directory.CreateDirectory(_dir);
        _donorPath = Path.Combine(_dir, DonorKey.FileName.String);
        _extPath = Path.Combine(_dir, ExtKey.FileName.String);

        var d = new SkyrimMod(DonorKey, SkyrimRelease.SkyrimSE);
        d.Weapons.Add(new Weapon(WaOld, SkyrimRelease.SkyrimSE) { EditorID = "HcWeapA", BasicStats = new WeaponBasicStats { Damage = 10 } });
        var fl = new FormList(FlOld, SkyrimRelease.SkyrimSE) { EditorID = "HcList" };
        fl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(WaOld));
        d.FormLists.Add(fl);
        d.ModHeader.Stats.NextFormID = 0xCCD;
        d.BeginWrite.ToPath(_donorPath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();

        using var donorOv = SkyrimMod.CreateFromBinaryOverlay(_donorPath, SkyrimRelease.SkyrimSE);
        var e = new SkyrimMod(ExtKey, SkyrimRelease.SkyrimSE);
        var exl = new FormList(new FormKey(ExtKey, 0x800), SkyrimRelease.SkyrimSE) { EditorID = "HcExtList" };
        exl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(WaOld));
        e.FormLists.Add(exl);
        e.ModHeader.Stats.NextFormID = 0x801;
        e.BeginWrite.ToPath(_extPath).WithLoadOrder(new[] { donorOv }).NoNextFormIDProcessing().Write();
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp cleanup best-effort */ } }

    RemapEngine.RemapPlan Plan()
    {
        using var ov = SkyrimMod.CreateFromBinaryOverlay(_donorPath, SkyrimRelease.SkyrimSE);
        var keys = ov.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == DonorKey).Select(r => r.FormKey).ToList();
        var plan = RemapEngine.BuildSequentialRemap(keys, DonorKey, RemapEngine.EslFloor, RemapEngine.EslCeiling);
        Assert.True(plan.Success, plan.Error);
        return plan;
    }

    // HAPPY: the plan moves each originating record to a NEW key in the window, in document order (0xAAA -> 0x800,
    // 0xCCC -> 0x801); the fixture starts inside the window, so the exact keys are what proves a renumber happened
    [Fact]
    public void ThePlanMovesEachRecordToTheNextFreeWindowId()
    {
        var plan = Plan();
        Assert.Equal(new FormKey(DonorKey, 0x800), plan.Dict[WaOld]);
        Assert.Equal(new FormKey(DonorKey, 0x801), plan.Dict[FlOld]);
    }

    // HAPPY: the renumbered P' re-reads with the weapon and list at their new keys and the INTERNAL ref repointed
    [Fact]
    public void TheRenumberedPluginHoldsTheNewKeysAndTheRepointedInternalRef()
    {
        var plan = Plan();
        var pPrimePath = Path.Combine(_dir, "pprime", DonorKey.FileName.String);
        Directory.CreateDirectory(Path.GetDirectoryName(pPrimePath)!);
        using (var donorOv = SkyrimMod.CreateFromBinaryOverlay(_donorPath, SkyrimRelease.SkyrimSE))
        {
            var pPrime = new SkyrimMod(DonorKey, SkyrimRelease.SkyrimSE);
            var ren = RemapEngine.RenumberRecordsInto(pPrime, donorOv.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == DonorKey), plan.Dict);
            Assert.True(ren.Success, ren.Error);
            pPrime.ModHeader.Stats.NextFormID = 0x802;
            WriteEngine.WriteInPlace(pPrime, Array.Empty<ISkyrimModGetter>(), pPrimePath, dataDir: null);
        }

        using var pp = SkyrimMod.CreateFromBinaryOverlay(pPrimePath, SkyrimRelease.SkyrimSE);
        Assert.Equal(new[] { plan.Dict[WaOld] }, pp.Weapons.Select(w => w.FormKey).ToArray());
        Assert.Equal(new[] { plan.Dict[FlOld] }, pp.FormLists.Select(l => l.FormKey).ToArray());
        Assert.Equal(plan.Dict[WaOld], pp.FormLists.First().Items.First().FormKey);
    }

    // HAPPY: the identify pass names External (not Donor) as an external referencer of the weapon, with nothing unscannable
    [Fact]
    public void TheIdentifyPassNamesTheExternalReferencerNotTheDonor()
    {
        var plan = Plan();
        using var resolver = LoadOrderResolver.Build(new[] { _donorPath, _extPath });
        var id = RemapEngine.IdentifyExternalReferencers(resolver, plan.Dict.Keys.ToHashSet(),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DonorKey.FileName.String });

        Assert.True(id.HasExternalReferencers);
        Assert.Contains(ExtKey.FileName.String, id.ExternalPlugins, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(DonorKey.FileName.String, id.ExternalPlugins, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(id.Refs, r => r.Target == WaOld && string.Equals(r.Plugin, ExtKey.FileName.String, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, id.UnscannableRecords);
    }

    // HAPPY: the opt-in in-place repoint rewrites External's reference to the weapon's new key
    [Fact]
    public void RepointInPlaceRewritesTheExternalReference()
    {
        var plan = Plan();
        using (var resolver = LoadOrderResolver.Build(new[] { _donorPath, _extPath }))
        {
            var rep = RemapEngine.RepointInPlace(resolver, ExtKey.FileName.String, plan.Dict);
            Assert.True(rep.Success, rep.Error);
        }
        using var ee = SkyrimMod.CreateFromBinaryOverlay(_extPath, SkyrimRelease.SkyrimSE);
        Assert.Equal(plan.Dict[WaOld], ee.FormLists.First().Items.First().FormKey);
    }

    // CAPACITY: BuildSequentialRemap refuses LOUD when the record count overflows the window
    [Fact]
    public void AWindowOverflowIsRefused()
    {
        var keys = new[] { new FormKey(DonorKey, 1), new FormKey(DonorKey, 2), new FormKey(DonorKey, 3) };
        var plan = RemapEngine.BuildSequentialRemap(keys, DonorKey, 0x800, 0x801);
        Assert.False(plan.Success);
        Assert.Contains("overflow", plan.Error, StringComparison.OrdinalIgnoreCase);
    }

    // NESTED: RenumberRecordsInto refuses LOUD on a nested-only record (a Cell has no flat group)
    [Fact]
    public void ANestedOnlyRecordIsRefused()
    {
        var cellKey = new FormKey(DonorKey, 0xD00);
        var cell = new Cell(cellKey, SkyrimRelease.SkyrimSE) { EditorID = "HcCell" };
        var ren = RemapEngine.RenumberRecordsInto(new SkyrimMod(DonorKey, SkyrimRelease.SkyrimSE),
            new IMajorRecordGetter[] { cell }, new Dictionary<FormKey, FormKey> { [cellKey] = new FormKey(DonorKey, 0x800) });
        Assert.False(ren.Success);
        Assert.Contains("NESTED", ren.Error, StringComparison.OrdinalIgnoreCase);
    }

    // ABSTRACT: a GlobalFloat renumbers and is placed through the abstract-group arm, at the new key only
    [Fact]
    public void AGlobalRenumbersIntoTheAbstractGroup()
    {
        var gOld = new FormKey(DonorKey, 0xBBB);
        var gNew = new FormKey(DonorKey, 0x800);
        var target = new SkyrimMod(DonorKey, SkyrimRelease.SkyrimSE);
        var ren = RemapEngine.RenumberRecordsInto(target,
            new IMajorRecordGetter[] { new GlobalFloat(gOld, SkyrimRelease.SkyrimSE) { EditorID = "HcGlobalF", Data = 2.5f } },
            new Dictionary<FormKey, FormKey> { [gOld] = gNew });
        Assert.True(ren.Success, ren.Error);
        Assert.Equal(1, ren.RecordsRenumbered);
        Assert.True(target.Globals.ContainsKey(gNew));
        Assert.False(target.Globals.ContainsKey(gOld));
        Assert.IsType<GlobalFloat>(target.Globals.First());
    }

    // OVERRIDE: compacting renumbers the ORIGINATING record but copies an override at its master's key
    [Fact]
    public void AnOverrideIsCopiedAtItsMastersKey()
    {
        var baseKey = new ModKey("HcRemapBase", ModType.Master);
        var wbKey = new FormKey(baseKey, 0x801);
        var basePath = Path.Combine(_dir, baseKey.FileName.String);
        var b = new SkyrimMod(baseKey, SkyrimRelease.SkyrimSE);
        b.Weapons.Add(new Weapon(wbKey, SkyrimRelease.SkyrimSE) { EditorID = "HcWeapB", BasicStats = new WeaponBasicStats { Damage = 7 } });
        b.ModHeader.Stats.NextFormID = 0x802;
        b.BeginWrite.ToPath(basePath).WithLoadOrder(Array.Empty<ISkyrimModGetter>()).NoNextFormIDProcessing().Write();

        var overKey = new ModKey("HcRemapOver", ModType.Plugin);
        var woOld = new FormKey(overKey, 0xAAA);
        var overPath = Path.Combine(_dir, overKey.FileName.String);
        using (var baseOv = SkyrimMod.CreateFromBinaryOverlay(basePath, SkyrimRelease.SkyrimSE))
        {
            var o = new SkyrimMod(overKey, SkyrimRelease.SkyrimSE);
            o.Weapons.GetOrAddAsOverride(baseOv.Weapons.First(w => w.FormKey == wbKey));
            o.Weapons.Add(new Weapon(woOld, SkyrimRelease.SkyrimSE) { EditorID = "HcWeapO", BasicStats = new WeaponBasicStats { Damage = 9 } });
            o.ModHeader.Stats.NextFormID = 0xAAB;
            o.BeginWrite.ToPath(overPath).WithLoadOrder(new[] { baseOv }).NoNextFormIDProcessing().Write();
        }

        using var overOv = SkyrimMod.CreateFromBinaryOverlay(overPath, SkyrimRelease.SkyrimSE);
        var origKeys = overOv.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == overKey).Select(r => r.FormKey).ToList();
        var plan = RemapEngine.BuildSequentialRemap(origKeys, overKey, RemapEngine.EslFloor, RemapEngine.EslCeiling);
        Assert.True(plan.Success, plan.Error);
        var pPrime = new SkyrimMod(overKey, SkyrimRelease.SkyrimSE);
        var ren = RemapEngine.RenumberRecordsInto(pPrime, overOv.EnumerateMajorRecords(), plan.Dict);

        Assert.True(ren.Success, ren.Error);
        Assert.Equal(2, ren.RecordsCopied);
        Assert.Equal(1, ren.RecordsRenumbered);
        Assert.True(pPrime.Weapons.ContainsKey(plan.Dict[woOld]));
        Assert.True(pPrime.Weapons.ContainsKey(wbKey));
    }

    // REFUSAL: RepointInPlace fails LOUD on a name not in the order, and on an empty remap dict
    [Fact]
    public void RepointInPlaceRefusesAnInactiveNameAndAnEmptyDict()
    {
        using var resolver = LoadOrderResolver.Build(new[] { _donorPath, _extPath });
        var notActive = RemapEngine.RepointInPlace(resolver, "HcDoesNotExist.esp",
            new Dictionary<FormKey, FormKey> { [WaOld] = new FormKey(DonorKey, 0x800) });
        var empty = RemapEngine.RepointInPlace(resolver, ExtKey.FileName.String, new Dictionary<FormKey, FormKey>());

        Assert.False(notActive.Success);
        Assert.Contains("not an active plugin", notActive.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(empty.Success);
        Assert.Contains("no remap", empty.Error, StringComparison.OrdinalIgnoreCase);
    }
}
