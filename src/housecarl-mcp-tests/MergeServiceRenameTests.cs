using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The single-donor merge is a rename (#345), and the header notes are keyed on what the donors carried
/// (the former merge-service-guard arms RENAME, the single-donor side of SHAPE, and HEADER).</summary>
[Trait("tier", "integration")]
[Collection("merge-service")]
public sealed class MergeServiceRenameTests
{
    readonly MergeServiceWorld _w;
    public MergeServiceRenameTests(MergeServiceWorld w) => _w = w;

    static FormKey R(uint id) => new(MergeServiceWorld.RenamedKey, id);

    WritePatchBuilder.MergeOutcome Ok()
    {
        Assert.True(_w.Renamed.Success, _w.Renamed.Error);
        return _w.Renamed;
    }

    string Rendered() => WriteTools.RenderMerge(Ok());

    static string Render(WritePatchBuilder.MergeOutcome o)
    {
        Assert.True(o.Success, o.Error);
        return WriteTools.RenderMerge(o);
    }

    // RENAME single donor accepted
    [Fact]
    public void ASingleDonorIsAccepted() => Assert.Single(Ok().Donors);

    // RENAME every A record remaps to the output ModKey at its own object id, none left on HcMgA.esp
    [Fact]
    public void EveryDonorRecordRemapsToTheOutputKeyAtItsOwnId()
    {
        using var rm = SkyrimMod.CreateFromBinaryOverlay(Ok().OutputPath, SkyrimRelease.SkyrimSE);
        var present = rm.EnumerateMajorRecords().Select(r => r.FormKey).ToHashSet();
        foreach (var id in new[] { 0xA01u, 0xA10u, 0xA11u, 0xA12u, 0xA20u, 0xA30u, 0xA40u, 0xA41u })
            Assert.Contains(R(id), present);
        Assert.Contains(rm.Weapons, w => w.EditorID == "HcMgAWeap" && w.FormKey == R(0xA01));
        Assert.DoesNotContain(present, k => k.ModKey == MergeServiceWorld.AKey);
    }

    // RENAME masters = base only, the donor is not a master of its own rename
    [Fact]
    public void TheMastersAreTheBaseOnly()
    {
        using var rm = SkyrimMod.CreateFromBinaryOverlay(Ok().OutputPath, SkyrimRelease.SkyrimSE);
        var master = Assert.Single(rm.ModHeader.MasterReferences);
        Assert.Equal(MergeServiceWorld.BaseKey.FileName.String, master.Master.FileName.String, ignoreCase: true);
    }

    // RENAME no collisions with one donor: A's in-window ids are all kept
    [Fact]
    public void AllInWindowIdsAreKept()
    {
        var r1 = Ok().DonorRemaps.Single(d => d.Donor == "HcMgA.esp");
        Assert.Equal((8, 0), (r1.Kept, r1.Renumbered));
    }

    // RENAME accounting: 8 originating + 1 base override (copied 9, renumbered 8)
    [Fact]
    public void TheAccountingCountsOriginatingRecordsAndTheOverride()
    {
        Assert.Equal(9, Ok().RecordsCopied);
        Assert.Equal(8, Ok().RecordsRenumbered);
    }

    // RENAME facegen pair carried to the renamed-name folder
    [Fact]
    public void TheFacegenPairIsCarriedToTheRenamedFolder()
    {
        var outDir = Path.GetDirectoryName(Ok().OutputPath)!;
        var face = FaceGenPath.Both(R(0xA20)).ToList();
        Assert.Equal(2, face.Count);
        Assert.All(face, x => Assert.True(File.Exists(Path.Combine(outDir, x.Item2)), x.Item2));
        Assert.Equal(2, Ok().AssetRename?.FacegenFilesCarried);
    }

    // RENAME voice carried to the renamed-name folder
    [Fact]
    public void TheVoiceFileIsCarriedToTheRenamedFolder()
    {
        var outDir = Path.GetDirectoryName(Ok().OutputPath)!;
        Assert.True(File.Exists(Path.Combine(outDir, "Sound", "Voice", "HcMgRenamed.esp", "MaleEvenToned", MergeServiceWorld.VoiceFile)));
        Assert.Equal(1, Ok().VoiceRename?.FilesCarried);
    }

    // RENAME .seq regenerated under the new name
    [Fact]
    public void TheSeqIsRegeneratedUnderTheNewName()
    {
        Assert.True(Ok().SeqRegen?.Written);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Ok().OutputPath)!, "SEQ", "HcMgRenamed.seq")));
    }

    // RENAME external referencers named, including the donor's own patch
    [Fact]
    public void TheReferencersIncludeTheDonorsOwnPatch()
    {
        Assert.Contains("HcMgDep.esp", Ok().ExternalPlugins);
        Assert.Contains("HcMgB.esp", Ok().ExternalPlugins);
    }

    // RENAME external overriders named, including the donor's own patch
    [Fact]
    public void TheOverridersIncludeTheDonorsOwnPatch()
    {
        Assert.Contains("HcMgOvr.esp", Ok().ExternalOverriders);
        Assert.Contains("HcMgB.esp", Ok().ExternalOverriders);
    }

    // RENAME headline names the operation and never says '1 donors'
    [Fact]
    public void TheHeadlineNamesARename()
    {
        var r = Rendered();
        Assert.Contains("a RENAME of HcMgA.esp", r);
        Assert.DoesNotContain("1 donors", r);
    }

    // RENAME headline's record claim is derived from the accounting, not asserted beside it
    [Fact]
    public void TheHeadlinesRecordClaimComesFromTheAccounting()
        => Assert.Contains("8 records move to the new plugin's identity", Rendered());

    // RENAME per-donor line names ONLY the cause that can apply to one donor
    [Fact]
    public void ThePerDonorLineNamesOnlyTheBelowFloorCause()
    {
        var r = Rendered();
        Assert.Contains("renumbered (below-floor)", r);
        Assert.DoesNotContain("id collisions / below-floor", r);
    }

    // RENAME external-referencer remedy leads with re-point, combining offered second
    [Fact]
    public void TheReferencerRemedyLeadsWithRepoint()
    {
        var r = Rendered();
        Assert.Contains("re-point them at 'HcMgRenamed.esp' before the swap", r);
        var combine = r.IndexOf("re-run with them added as donors", StringComparison.Ordinal);
        Assert.True(combine > r.IndexOf("re-point them at", StringComparison.Ordinal), "re-point must come before combining");
    }

    // RENAME external-overrider remedy leads with rebuild
    [Fact]
    public void TheOverriderRemedyLeadsWithRebuild()
        => Assert.Contains("Rebuild them against 'HcMgRenamed.esp'", Rendered());

    // RENAME folder and plugin both take patch=
    [Fact]
    public void TheOutputFolderAndPluginBothTakeTheNewName()
    {
        Assert.Equal("houseCARL - HcMgRenamed", Path.GetFileName(Path.GetDirectoryName(Ok().OutputPath)));
        Assert.Equal("HcMgRenamed.esp", Path.GetFileName(Ok().OutputPath));
    }

    // RENAME still carries the saves warning and the MO2 swap instruction
    [Fact]
    public void TheSavesWarningAndSwapInstructionStay()
    {
        var r = Rendered();
        Assert.Contains("existing SAVES", r);
        Assert.Contains("deactivate the donor PLUGINS", r);
    }

    // RENAME a below-floor id renumbers even with nothing to collide with (planner measured directly)
    [Fact]
    public void ABelowFloorIdRenumbersWithNothingToCollideWith()
    {
        var soloKey = new ModKey("HcMgSolo", ModType.Plugin);
        var soloOut = new ModKey("HcMgSoloRenamed", ModType.Plugin);
        var donors = new List<(string Donor, IReadOnlyList<FormKey> Keys)>
        {
            ("HcMgSolo.esp", new[] { new FormKey(soloKey, 0xC01), new FormKey(soloKey, 0x123) })
        };
        var solo = RemapEngine.BuildMergeRemap(donors, soloOut, RemapEngine.EslFloor, FormIdRange.ObjectIdMax);

        Assert.True(solo.Success);
        Assert.Equal(new FormKey(soloOut, 0xC01), solo.Dict[new FormKey(soloKey, 0xC01)]);
        var moved = solo.Dict[new FormKey(soloKey, 0x123)];
        Assert.Equal(soloOut, moved.ModKey);
        Assert.True(moved.ID >= RemapEngine.EslFloor, $"0x{moved.ID:X}");
        var d = Assert.Single(solo.Donors);
        Assert.Equal((1, 1), (d.Kept, d.Renumbered));
    }

    // RENAME the donor's light flag is carried onto the output, its header text is not
    // RENAME the carried light flag is REPORTED
    // RENAME the header-text loss is stated bare, no remedy invented for it
    // RENAME the flat 'want it light' tail steps aside when the output is already light
    [Fact]
    public void ALightDonorsFlagIsCarriedAndReportedAndItsHeaderTextIsNot()
    {
        var o = _w.Svc.MergePlugins(new[] { "HcMgEsl.esp" }, "HcMgEslRenamed.esp");
        var r = Render(o);
        using (var em = SkyrimMod.CreateFromBinaryOverlay(o.OutputPath, SkyrimRelease.SkyrimSE))
        {
            Assert.True(em.IsSmallMaster);
            Assert.True(string.IsNullOrEmpty(em.ModHeader.Author));
            Assert.True(string.IsNullOrEmpty(em.ModHeader.Description));
        }
        Assert.True(o.LightCarried);
        Assert.Contains("is written LIGHT too", r);
        Assert.Contains("header Author/Description carried by HcMgEsl.esp", r);
        Assert.DoesNotContain("housecarl_create_plugin on", r);
        Assert.DoesNotContain("Want it light?", r);
    }

    // RENAME a .esl-EXTENSION donor counts as light even with the header bit unset
    [Fact]
    public void AnEslExtensionDonorCountsAsLightWithoutTheBit()
    {
        var o = _w.Svc.MergePlugins(new[] { "HcMgEslExt.esl" }, "HcMgEslExtRenamed.esp");
        var r = Render(o);
        Assert.True(o.LightCarried);
        Assert.Contains("is written LIGHT too", r);
    }

    // RENAME the master-status loss is stated, with no remedy invented
    [Fact]
    public void TheMasterStatusLossIsStatedWithNoRemedy()
    {
        var r = Render(_w.Svc.MergePlugins(new[] { "HcMgEsm.esm" }, "HcMgEsmRenamed.esp"));
        Assert.Contains("HcMgEsm.esm carried MASTER status", r);
        Assert.Contains("is NOT flagged as a master", r);
        Assert.DoesNotContain("run housecarl_", r);
    }

    // MERGE all three header-loss notes fire on the MULTI-donor path too
    [Fact]
    public void AllThreeHeaderNotesFireOnAMultiDonorMerge()
    {
        var o = _w.Svc.MergePlugins(new[] { "HcMgEsl.esp", "HcMgEsm.esm" }, "HcMgMixed.esp");
        var r = Render(o);
        Assert.Equal(2, o.Donors.Count);
        Assert.False(o.LightCarried);
        Assert.Contains("carried the LIGHT (ESL) status", r);
        Assert.Contains("Not every donor was light (1 of 2 were)", r);
        Assert.Contains("carried MASTER status", r);
        Assert.Contains("header Author/Description carried by", r);
        Assert.Contains("from 2 donors", r);
    }

    // RENAME a pure-override donor states the override COUNT it read
    // RENAME the runtime-config sentence is unchanged by a donor that originates nothing
    [Fact]
    public void APureOverrideDonorStatesItsOverrideCount()
    {
        var o = _w.Svc.MergePlugins(new[] { "HcMgOvr.esp" }, "HcMgOvrRenamed.esp");
        var r = Render(o);
        Assert.Equal(0, o.RecordsRenumbered);
        Assert.Equal(1, o.RecordsCopied);
        Assert.Contains("its 1 override is now served by a plugin under a new name", r);
        Assert.DoesNotContain("records move to the new plugin's identity", r);
        Assert.Contains(WriteSentences.MergeRuntimeConfigs, r);
    }

    // RENAME an EMPTY donor claims neither moves nor overrides
    [Fact]
    public void AnEmptyDonorClaimsNeitherMovesNorOverrides()
    {
        var o = _w.Svc.MergePlugins(new[] { "HcMgEmpty.esp" }, "HcMgEmptyRenamed.esp");
        var r = Render(o);
        Assert.Equal(0, o.RecordsCopied);
        Assert.Equal(0, o.RecordsRenumbered);
        Assert.Contains("it carries no records at all, so nothing moved and nothing is overridden", r);
        Assert.DoesNotContain("override is now served", r);
        Assert.DoesNotContain("overrides are now served", r);
    }

    // RENAME a donor named twice is ONE donor, still a rename
    [Fact]
    public void ADonorNamedTwiceIsOneDonor()
    {
        var o = _w.Svc.MergePlugins(new[] { "HcMgA.esp", "HcMgA.esp" }, "HcMgDup.esp");
        Assert.True(o.Success, o.Error);
        Assert.Single(o.Donors);
    }

    // RENAME donor names differing only by CASE are ONE donor
    [Fact]
    public void DonorNamesDifferingOnlyByCaseAreOneDonor()
    {
        var o = _w.Svc.MergePlugins(new[] { "HcMgA.esp", "HCMGA.ESP" }, "HcMgCase.esp");
        var r = Render(o);
        Assert.Single(o.Donors);
        Assert.Empty(o.Conflicts);
        Assert.Contains("a RENAME of", r);
    }
}
