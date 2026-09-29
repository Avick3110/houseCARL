using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The compact service lane's policy branches (<see cref="LoadOrderService.CompactPlugin"/>) over a
/// synthetic instance: new-file and in-place compacts, the light flag, the refusals, the consent prompt. Each test
/// builds its own world, because several compact in place or add plugins.</summary>
[Trait("tier", "integration")]
public sealed class CompactServicePolicyTests
{
    // CLEAN new-file compact: success, not in place, every originating record in the ESL window, light flag set.
    [Fact]
    public void ASelfContainedPluginCompactsToANewLightFileInTheEslWindow()
    {
        using var w = new CompactServiceWorld();
        var o = w.Svc.CompactPlugin("HcCsSelf.esp");

        Assert.True(o.Success, o.Error);
        Assert.False(o.InPlace);
        Assert.True(o.Esl);
        Assert.Equal(3, o.RecordsRenumbered);
        Assert.Empty(o.ExternalPlugins);
        using var pp = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
        Assert.True(pp.IsSmallMaster);
        // The next free id sits just above the renumbered run.
        Assert.Equal(RemapEngine.EslFloor + 3, pp.ModHeader.Stats.NextFormID);
        Assert.All(pp.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == CompactServiceWorld.SelfKey),
            r => Assert.True(CompactServiceWorld.InEslWindow(r.FormKey), r.FormKey.ToString()));
    }

    // CLEAN the runtime-config loss reaches user output, verbatim from the shared sentence.
    [Fact]
    public void TheCompactReportCarriesTheRuntimeConfigSentence()
    {
        using var w = new CompactServiceWorld();
        var o = w.Svc.CompactPlugin("HcCsSelf.esp");

        Assert.True(o.Success, o.Error);
        Assert.Contains(WriteSentences.CompactRuntimeConfigs, WriteTools.RenderCompact(o));
    }

    // Not a probe assert: the probe compacted HcCsSelf.esp three times in one instance, which is what reached the
    // taken-folder arm of the output folder picker. A second compact lands beside the first, never over it.
    [Fact]
    public void ASecondCompactOfTheSamePluginLandsInItsOwnFolder()
    {
        using var w = new CompactServiceWorld();
        var first = w.Svc.CompactPlugin("HcCsSelf.esp");
        Assert.True(first.Success, first.Error);
        var firstBytes = File.ReadAllBytes(first.OutputPath);

        var second = w.Svc.CompactPlugin("HcCsSelf.esp", esl: false);

        Assert.True(second.Success, second.Error);
        Assert.NotEqual(Path.GetDirectoryName(first.OutputPath), Path.GetDirectoryName(second.OutputPath));
        Assert.True(LocalizedCompactWorld.Same(first.OutputPath, firstBytes));
    }

    // ESL-OFF contiguous renumber, no light flag.
    [Fact]
    public void EslFalseRenumbersWithoutTheLightFlag()
    {
        using var w = new CompactServiceWorld();
        var o = w.Svc.CompactPlugin("HcCsSelf.esp", esl: false);

        Assert.True(o.Success, o.Error);
        Assert.False(o.Esl);
        using var pp = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
        Assert.False(pp.IsSmallMaster);
    }

    // OVERRIDE master-cell preserved + new child renumbered (copied 2, renum 1).
    [Fact]
    public void AnOverrideCellKeepsItsMasterKeyWhileTheNewPlacedRefRenumbers()
    {
        using var w = new CompactServiceWorld();
        var o = w.Svc.CompactPlugin("HcCsOver.esp");

        Assert.True(o.Success, o.Error);
        Assert.Equal(2, o.RecordsCopied);
        Assert.Equal(1, o.RecordsRenumbered);
        using var pp = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
        var cell = pp.EnumerateMajorRecords<ICellGetter>().Single(c => c.EditorID == "HcCsBaseCell");
        Assert.Equal(CompactServiceWorld.BaseCell, cell.FormKey);
        var placed = cell.Temporary.Single(p => p.EditorID == "HcCsOverRef");
        Assert.Equal(CompactServiceWorld.OverKey, placed.FormKey.ModKey);
        Assert.True(CompactServiceWorld.InEslWindow(placed.FormKey), placed.FormKey.ToString());
    }

    // REFUSE-EXT external referencer named.
    [Fact]
    public void APluginAnotherPluginReferencesIsRefusedNamingTheReferencer()
    {
        using var w = new CompactServiceWorld();
        var o = w.Svc.CompactPlugin("HcCsLib.esp");

        Assert.False(o.Success);
        Assert.False(o.NeedsAcknowledge);
        Assert.Contains("HcCsDep.esp", o.Error, StringComparison.OrdinalIgnoreCase);
    }

    // GATE repoint_externals requires in_place.
    [Fact]
    public void RepointExternalsWithoutInPlaceIsRefused()
    {
        using var w = new CompactServiceWorld();
        var o = w.Svc.CompactPlugin("HcCsLib.esp", repointExternals: true, inPlace: false);

        Assert.False(o.Success);
        Assert.Contains("requires in_place", o.Error, StringComparison.OrdinalIgnoreCase);
    }

    // NOT-ACTIVE nowhere-on-disk plugin refused.
    [Fact]
    public void APluginFoundNowhereOnDiskIsRefused()
    {
        using var w = new CompactServiceWorld();
        var o = w.Svc.CompactPlugin("HcCsNope.esp");

        Assert.False(o.Success);
        Assert.Contains("not an active plugin", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no on-disk copy", o.Error, StringComparison.OrdinalIgnoreCase);
    }

    // OFF-ORDER unlisted-folder plugin compacts, noted.
    [Fact]
    public void APluginInAnUnlistedModFolderCompactsWithTheOffOrderNote()
    {
        using var w = new CompactServiceWorld();
        var offKey = new ModKey("HcCsOff", ModType.Plugin);
        w.WriteMod("OffOrderMod", offKey, m =>
            m.Weapons.Add(new Weapon(new FormKey(offKey, 0x1A01), SkyrimRelease.SkyrimSE) { EditorID = "HcCsOffWeap", BasicStats = new WeaponBasicStats { Damage = 3 } }));

        var o = w.Svc.CompactPlugin("HcCsOff.esp");

        Assert.True(o.Success, o.Error);
        Assert.Equal(1, o.RecordsRenumbered);
        Assert.Contains("OFF-ORDER", o.Note);
        using var pp = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
        Assert.True(pp.IsSmallMaster);
        Assert.All(pp.EnumerateMajorRecords(), r => Assert.True(CompactServiceWorld.InEslWindow(r.FormKey), r.FormKey.ToString()));
    }

    // FLAG-ONLY esl=false: refused, nothing to do.
    [Fact]
    public void AnOverrideOnlyPluginWithEslFalseIsRefusedAsNothingToCompact()
    {
        using var w = new CompactServiceWorld();
        w.WriteFlagOnlyMod(new ModKey("HcCsFlag", ModType.Plugin));

        var o = w.Svc.CompactPlugin("HcCsFlag.esp", esl: false);

        Assert.False(o.Success);
        Assert.Contains("nothing to compact", o.Error, StringComparison.OrdinalIgnoreCase);
    }

    // FLAG-ONLY esl=true: verbatim copy + light flag (renum 0, "no originating records" note).
    [Fact]
    public void AnOverrideOnlyPluginWithEslTrueIsCopiedVerbatimAndFlaggedLight()
    {
        using var w = new CompactServiceWorld();
        w.WriteFlagOnlyMod(new ModKey("HcCsFlag", ModType.Plugin));

        var o = w.Svc.CompactPlugin("HcCsFlag.esp");

        Assert.True(o.Success, o.Error);
        Assert.Equal(0, o.RecordsRenumbered);
        Assert.Contains("no originating records", o.Note);
        using var pp = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE);
        Assert.True(pp.IsSmallMaster);
        var weap = pp.EnumerateMajorRecords<IWeaponGetter>().Single();
        Assert.Equal(CompactServiceWorld.LibWeapon, weap.FormKey);
        Assert.Equal((ushort)42, weap.BasicStats?.Damage);
    }

    // OFF-ORDER-AMBIGUOUS: a basename two folders provide is refused, named.
    [Fact]
    public void ABasenameTwoUnlistedFoldersProvideIsRefusedAsAmbiguous()
    {
        using var w = new CompactServiceWorld();
        w.WriteFlagOnlyMod(new ModKey("HcCsFlag", ModType.Plugin));
        var first = w.Svc.CompactPlugin("HcCsFlag.esp");
        Assert.True(first.Success, first.Error);

        // The compacted output is a second on-disk copy of the same basename.
        var o = w.Svc.CompactPlugin("HcCsFlag.esp");

        Assert.False(o.Success);
        Assert.Contains("ambiguous", o.Error, StringComparison.OrdinalIgnoreCase);
    }

    // CONSENT in_place+repoint without ack returns CONFIRM listing target+external.
    [Fact]
    public void InPlaceRepointWithoutAcknowledgeAsksToConfirmNamingTargetAndReferencer()
    {
        using var w = new CompactServiceWorld();
        var libPath = Path.Combine(w.Mods, "LibMod", "HcCsLib.esp");
        var before = File.ReadAllBytes(libPath);

        var o = w.Svc.CompactPlugin("HcCsLib.esp", repointExternals: true, inPlace: true, acknowledge: false);

        Assert.False(o.Success);
        Assert.True(o.NeedsAcknowledge);
        Assert.Contains("HcCsLib.esp", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HcCsDep.esp", o.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(LocalizedCompactWorld.Same(libPath, before));
    }

    // CONSENT in_place+repoint with ack: Lib weapon renumbered into the window, Dep's ref follows it.
    [Fact]
    public void InPlaceRepointWithAcknowledgeCompactsTheTargetAndRepointsTheReferencer()
    {
        using var w = new CompactServiceWorld();
        var o = w.Svc.CompactPlugin("HcCsLib.esp", repointExternals: true, inPlace: true, acknowledge: true);

        Assert.True(o.Success, o.Error);
        Assert.True(o.InPlace);
        var r = Assert.Single(o.Repointed);
        Assert.True(r.Success);
        FormKey libWeap, depRef;
        using (var lb = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(w.Mods, "LibMod", "HcCsLib.esp"), SkyrimRelease.SkyrimSE))
            libWeap = lb.Weapons.Single(x => x.EditorID == "HcCsLibWeap").FormKey;
        using (var db = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(w.Mods, "DepMod", "HcCsDep.esp"), SkyrimRelease.SkyrimSE))
            depRef = db.FormLists.First().Items.First().FormKey;
        Assert.Equal(CompactServiceWorld.LibKey, libWeap.ModKey);
        Assert.True(CompactServiceWorld.InEslWindow(libWeap), libWeap.ToString());
        Assert.Equal(libWeap, depRef);
    }

    // NEWFILE-NOTE a NON-localized source compacts to a new file with no localization note.
    [Fact]
    public void ANonLocalizedSourceGetsNoLocalizationNote()
    {
        using var w = new CompactServiceWorld();
        var o = w.Svc.CompactPlugin("HcCsSelf.esp");

        Assert.True(o.Success, o.Error);
        Assert.DoesNotContain("localized", o.Note ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
