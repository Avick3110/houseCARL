using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The multi-donor merge of a mod with its patch (the former merge-service-guard arms MERGE / WINNER / GRAFT /
/// MOVED-REF / WARN / ASSETS / UNTOUCHED, and the multi-donor side of SHAPE and HEADER).</summary>
[Trait("tier", "integration")]
[Collection("merge-service")]
public sealed class MergeServiceCombineTests
{
    readonly MergeServiceWorld _w;
    public MergeServiceCombineTests(MergeServiceWorld w) => _w = w;

    static FormKey M(uint id) => new(MergeServiceWorld.MergedKey, id);

    ISkyrimModDisposableGetter Output()
    {
        Assert.True(_w.Merged.Success, _w.Merged.Error);
        return SkyrimMod.CreateFromBinaryOverlay(_w.Merged.OutputPath, SkyrimRelease.SkyrimSE);
    }

    string Rendered() => WriteTools.RenderMerge(_w.Merged);

    // MERGE A + B keep their non-colliding object ids under the merged key
    [Fact]
    public void BothDonorsKeepTheirNonCollidingIdsUnderTheMergedKey()
    {
        using var mm = Output();
        Assert.Contains(mm.Weapons, w => w.EditorID == "HcMgAWeap" && w.FormKey == M(0xA01));
        Assert.Contains(mm.Weapons, w => w.EditorID == "HcMgBWeap" && w.FormKey == M(0xB01));
    }

    // MERGE B's colliding id renumbered to the first free id (0x800)
    [Fact]
    public void TheLaterDonorsCollidingIdRenumbersToTheFirstFreeId()
    {
        using var mm = Output();
        Assert.Contains(mm.Weapons, w => w.EditorID == "HcMgBColl" && w.FormKey == M(0x800));
    }

    // MERGE B's cross-donor reference into A repointed to the merged key
    [Fact]
    public void TheCrossDonorReferenceRepointsToTheMergedKey()
    {
        using var mm = Output();
        Assert.Equal(M(0xA01), mm.FormLists.Single(f => f.EditorID == "HcMgBList").Items.Single().FormKey);
    }

    // WINNER load-order winner everywhere (B's base-weapon override + DIAL body + INFO1 text)
    [Fact]
    public void TheLoadOrderWinnerSuppliesEveryConflictedRecord()
    {
        using var mm = Output();
        Assert.Equal((ushort?)20, mm.Weapons.Single(w => w.FormKey == MergeServiceWorld.BaseWeap).BasicStats?.Damage);
        var topic = mm.DialogTopics.Single(t => t.FormKey == M(0xA10));
        Assert.Equal("HcMgTopicPatched", topic.EditorID);
        Assert.Equal("A11 patched", topic.Responses.Single(r => r.FormKey == M(0xA11)).Responses.First().Text.String);
    }

    // GRAFT A's un-relisted INFO2 grafted into the winning topic
    [Fact]
    public void TheLosingDonorsUnrelistedInfoIsGraftedIntoTheWinningTopic()
    {
        using var mm = Output();
        var topic = mm.DialogTopics.Single(t => t.FormKey == M(0xA10));
        Assert.Equal("A12 base", topic.Responses.Single(r => r.FormKey == M(0xA12)).Responses.First().Text.String);
    }

    // MOVED-REF one copy at the merged key, under the WINNER's cell, conflict reported
    [Fact]
    public void AMovedRefSurvivesOnceUnderTheWinnersCellAndIsAReportedConflict()
    {
        using var mm = Output();
        var refKey = M(0xA40);
        var copy = Assert.Single(mm.EnumerateMajorRecords().Where(r => r.FormKey == refKey));
        Assert.Equal("HcMgRefMoved", copy.EditorID);
        var cells = mm.EnumerateMajorRecords<ICellGetter>().ToList();
        Assert.Contains(cells.Single(c => c.FormKey == M(0xB10)).Temporary, p => p.FormKey == refKey);
        Assert.DoesNotContain(cells.Single(c => c.FormKey == M(0xA41)).Temporary, p => p.FormKey == refKey);
        Assert.Contains(_w.Merged.Conflicts, c => c.Key == MergeServiceWorld.ARef);
    }

    // MERGE masters = base only, donors gone
    [Fact]
    public void TheMastersAreTheBaseOnlyAndNoDonor()
    {
        using var mm = Output();
        var master = Assert.Single(mm.ModHeader.MasterReferences);
        Assert.Equal(MergeServiceWorld.BaseKey.FileName.String, master.Master.FileName.String, ignoreCase: true);
    }

    // MERGE record accounting (copied 13, renumbered 12)
    [Fact]
    public void TheRecordAccountingCountsCopiedAndRenumbered()
    {
        Assert.Equal(13, _w.Merged.RecordsCopied);
        Assert.Equal(12, _w.Merged.RecordsRenumbered);
    }

    // WINNER all 4 cross-donor conflicts reported with winner/loser named
    [Fact]
    public void EveryCrossDonorConflictIsReportedWithWinnerAndLoserNamed()
    {
        var conflicts = _w.Merged.Conflicts;
        Assert.Equal(4, conflicts.Count);
        Assert.All(conflicts, c => { Assert.Equal("HcMgB.esp", c.WinnerDonor); Assert.Equal("HcMgA.esp", c.LoserDonor); });
        foreach (var k in new[] { MergeServiceWorld.ADial, MergeServiceWorld.AInfo1, MergeServiceWorld.BaseWeap, MergeServiceWorld.ARef })
            Assert.Contains(conflicts, c => c.Key == k);
    }

    // MERGE per-donor id accounting (A 8/0, B 3/1)
    [Fact]
    public void ThePerDonorIdAccountingSaysWhoKeptAndWhoRenumbered()
    {
        var ra = _w.Merged.DonorRemaps.Single(d => d.Donor == "HcMgA.esp");
        var rb = _w.Merged.DonorRemaps.Single(d => d.Donor == "HcMgB.esp");
        Assert.Equal((8, 0), (ra.Kept, ra.Renumbered));
        Assert.Equal((3, 1), (rb.Kept, rb.Renumbered));
    }

    // WARN external referencer + overrider named, merge NOT refused
    [Fact]
    public void TheExternalReferencerAndOverriderAreNamedAndTheMergeStillSucceeds()
    {
        Assert.True(_w.Merged.Success, _w.Merged.Error);
        Assert.Contains("HcMgDep.esp", _w.Merged.ExternalPlugins);
        Assert.Contains("HcMgOvr.esp", _w.Merged.ExternalOverriders);
    }

    // WARN both warnings reach the rendered user output
    [Fact]
    public void BothWarningsReachTheRenderedOutput()
    {
        var r = Rendered();
        Assert.Contains("WARNING", r);
        Assert.Contains("HcMgDep.esp", r);
        Assert.Contains("HcMgOvr.esp", r);
    }

    // WARN the identify-pass line states what the pass does and does NOT read
    [Fact]
    public void TheIdentifyPassLineStatesWhatItDoesNotRead()
    {
        var r = Rendered();
        Assert.Contains("NOT runtime config files", r);
        Assert.Contains("only names a donor in such a file", r);
    }

    // WARN the runtime-config loss reaches user output, verbatim from the shared sentence
    [Fact]
    public void TheRuntimeConfigLossReachesTheRenderedOutput()
        => Assert.Contains(WriteSentences.MergeRuntimeConfigs, Rendered());

    // SWAP instruction is plugin-level (donor mod folders stay enabled)
    [Fact]
    public void TheSwapInstructionIsPluginLevel()
    {
        var r = Rendered();
        Assert.Contains("deactivate the donor PLUGINS", r);
        Assert.Contains("KEEP the donor mod folders enabled", r);
        Assert.DoesNotContain("DISABLE the donor mods", r);
    }

    // MERGE headline is the multi-donor form, never the rename arm
    [Fact]
    public void TheHeadlineIsTheMultiDonorForm()
    {
        var r = Rendered();
        Assert.Contains("from 2 donors", r);
        Assert.DoesNotContain("a RENAME of", r);
    }

    // MERGE per-donor line names BOTH renumber causes (donors can collide)
    [Fact]
    public void ThePerDonorLineNamesBothRenumberCauses()
        => Assert.Contains("id collisions / below-floor", Rendered());

    // MERGE external-referencer remedy leads with include-in-set
    [Fact]
    public void TheReferencerRemedyLeadsWithIncludeInSet()
        => Assert.Contains("include them in the merge set (re-run with them added), or re-point them", Rendered());

    // MERGE external-overrider remedy leads with include-in-set
    [Fact]
    public void TheOverriderRemedyLeadsWithIncludeInSet()
        => Assert.Contains("Include them in the merge set, or rebuild them against", Rendered());

    // MERGE no header-loss notes when no donor carried any of those header properties
    [Fact]
    public void NoHeaderLossNoteWhenNoDonorCarriedThoseProperties()
    {
        var r = Rendered();
        Assert.DoesNotContain("LIGHT (ESL) status", r);
        Assert.DoesNotContain("MASTER status", r);
        Assert.DoesNotContain("Author/Description", r);
    }

    // MERGE keeps the closing compact pointer when no qualified ESL note replaced it
    [Fact]
    public void TheClosingCompactPointerStaysWhenTheOutputIsNotLight()
        => Assert.Contains("Want it light?", Rendered());

    // MERGE folder and plugin both take patch=
    [Fact]
    public void TheOutputFolderAndPluginBothTakeTheOutputName()
    {
        Assert.True(_w.Merged.Success, _w.Merged.Error);
        Assert.Equal("houseCARL - HcMgMerged", Path.GetFileName(Path.GetDirectoryName(_w.Merged.OutputPath)));
        Assert.Equal("HcMgMerged.esp", Path.GetFileName(_w.Merged.OutputPath));
    }

    // ASSETS facegen pair carried to the merged-name folder
    [Fact]
    public void TheFacegenPairIsCarriedToTheMergedNameFolder()
    {
        var outDir = Path.GetDirectoryName(_w.Merged.OutputPath)!;
        var face = FaceGenPath.Both(M(0xA20)).ToList();
        Assert.Equal(2, face.Count);
        Assert.All(face, x => Assert.True(File.Exists(Path.Combine(outDir, x.Item2)), x.Item2));
        Assert.Equal(2, _w.Merged.AssetRename?.FacegenFilesCarried);
    }

    // ASSETS voice carried to the merged-name folder
    [Fact]
    public void TheVoiceFileIsCarriedToTheMergedNameFolder()
    {
        var outDir = Path.GetDirectoryName(_w.Merged.OutputPath)!;
        Assert.True(File.Exists(Path.Combine(outDir, "Sound", "Voice", "HcMgMerged.esp", "MaleEvenToned", MergeServiceWorld.VoiceFile)));
        Assert.Equal(1, _w.Merged.VoiceRename?.FilesCarried);
    }

    // ASSETS .seq regenerated for the merged plugin
    [Fact]
    public void TheSeqIsRegeneratedForTheMergedPlugin()
    {
        Assert.True(_w.Merged.SeqRegen?.Written);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(_w.Merged.OutputPath)!, "SEQ", "HcMgMerged.seq")));
    }

    // UNTOUCHED donor files byte-identical after the merge (and RENAME donor file byte-identical)
    [Fact]
    public void TheDonorFilesAreByteIdenticalAfterTheMergeAndTheRename()
    {
        Assert.True(_w.Merged.Success && _w.Renamed.Success);
        Assert.Equal(_w.ABytesBefore, File.ReadAllBytes(_w.APath));
        Assert.Equal(_w.BBytesBefore, File.ReadAllBytes(_w.BPath));
    }
}
