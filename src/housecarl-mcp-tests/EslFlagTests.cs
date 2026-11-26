using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary><c>housecarl_esl_flag</c> on houseCARL's own patches, the offer a write into one that fits ends with, and
/// the refusal a write gets that would take a light patch past its window. Each test builds its own world: every one writes.</summary>
[Trait("tier", "integration")]
[Collection("records")]
public sealed class EslFlagTests
{
    static CreateOp Kw(string eid) => new() { RecordType = "Keyword", Editorid = eid };

    static (bool Light, HashSet<uint> Own) Read(string path)
    {
        using var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return (m.IsSmallMaster, m.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == m.ModKey).Select(r => r.FormKey.ID).ToHashSet());
    }

    static string NewPatch(RecordsWorld w, string stem, params CreateOp[] records)
    {
        var o = w.Svc.CreateRecordsBatch(records, stem, null);
        Assert.True(o.Success, o.Error);
        return o.OutputPath;
    }

    /// <summary>A houseCARL patch written by hand: header <paramref name="version"/>, the world's master, one own keyword at <paramref name="ownId"/>.</summary>
    static string HandMadePatch(RecordsWorld w, string stem, float version, uint ownId)
    {
        var folder = Path.Combine(w.ModsDir, "houseCARL - " + stem);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "meta.ini"), $"{HousecarlOwnerMeta.Section}\r\ngenerated=true\r\nplugin={stem}.esp\r\n");
        var key = new ModKey(stem, ModType.Plugin);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.Stats.Version = version;
        mod.Keywords.Add(new Keyword(new FormKey(key, ownId), SkyrimRelease.SkyrimSE) { EditorID = stem + "_Kw" });
        mod.Weapons.GetOrAddAsOverride(w.Master.Weapons.First()).EditorID = stem + "_W";
        var path = Path.Combine(folder, stem + ".esp");
        mod.BeginWrite.ToPath(path).WithLoadOrder(new ISkyrimModGetter[] { w.Master })
            .NoCheckIfLowerRangeDisallowed().NoFormIDCompactnessCheck().NoNextFormIDProcessing().Write();
        return path;
    }

    // a patch with no records of its own flags, moving nothing
    [Fact]
    public void AnOverrideOnlyPatchFlagsLight()
    {
        using var w = new RecordsWorld();
        var o = w.Svc.ApplyEdits(new[] { new BulkOp { Formid = RecordsWorld.Fid(w.Weapons[0]), FieldPath = "BasicStats.Damage", Value = "5" } }, "HcLfOvr", null);
        Assert.True(o.Success, o.Error);

        var r = WriteTools.EslFlag(w.Svc, source: "HcLfOvr.esp");

        Assert.Contains("flagged HcLfOvr.esp light", r);
        Assert.True(Read(o.OutputPath).Light);
    }

    // the held shape: header 1.71 with a master and an own id of 001 fits only under Mutagen's rule, not a fixed 800-FFF
    [Fact]
    public void A171PatchWithAMasterAndOwnId001Flags()
    {
        using var w = new RecordsWorld();
        var path = HandMadePatch(w, "HcLfHand", 1.71f, 0x001);

        var r = WriteTools.EslFlag(w.Svc, source: "HcLfHand.esp");

        Assert.Contains("flagged HcLfHand.esp light", r);
        var back = Read(path);
        Assert.True(back.Light);
        Assert.Equal(new uint[] { 0x001 }, back.Own.Order());
    }

    // under a 1.70 header the window starts at 800, so the same own id 001 refuses and nothing is written
    [Fact]
    public void A170PatchWithOwnId001IsRefused()
    {
        using var w = new RecordsWorld();
        var path = HandMadePatch(w, "HcLfOld", 1.70f, 0x001);
        var before = File.ReadAllBytes(path);

        var r = WriteTools.EslFlag(w.Svc, source: "HcLfOld.esp");

        Assert.Contains("outside the light window 0x800–0xFFF", r);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // a plugin no houseCARL folder holds is refused, pointing at xEdit
    [Fact]
    public void APluginThatIsNotAHouseCarlPatchIsRefusedPointingAtXEdit()
    {
        using var w = new RecordsWorld();
        var before = File.ReadAllBytes(w.OverrideFile);

        var r = WriteTools.EslFlag(w.Svc, source: w.OverrideName);

        Assert.Contains("is not a houseCARL patch", r);
        Assert.Contains("tick ESL in xEdit's file header", r);
        Assert.Equal(before, File.ReadAllBytes(w.OverrideFile));
    }

    // a houseCARL folder holding two plugins is houseCARL's: the resolver's own refusal, not "not a houseCARL patch"
    [Fact]
    public void AnOwnedFolderWithTwoPluginsKeepsTheResolversRefusal()
    {
        using var w = new RecordsWorld();
        var a = NewPatch(w, "HcLfTwo", Kw("HcLfTwoKw"));
        File.Copy(a, Path.Combine(Path.GetDirectoryName(a)!, "HcLfTwoB.esp"));

        var r = WriteTools.EslFlag(w.Svc, source: "houseCARL - HcLfTwo");

        Assert.StartsWith("error: cannot extend", r);
        Assert.DoesNotContain("not a houseCARL patch", r);
    }

    // two houseCARL folders holding the same plugin: ambiguous, said as such
    [Fact]
    public void APluginTwoOwnedFoldersHoldIsAmbiguousNotForeign()
    {
        using var w = new RecordsWorld();
        var a = NewPatch(w, "HcLfDup", Kw("HcLfDupKw"));
        var other = Path.Combine(w.ModsDir, "HcLfDup Copy");
        Directory.CreateDirectory(other);
        File.Copy(a, Path.Combine(other, "HcLfDup.esp"));
        File.Copy(Path.Combine(Path.GetDirectoryName(a)!, "meta.ini"), Path.Combine(other, "meta.ini"));
        Directory.Move(Path.GetDirectoryName(a)!, Path.Combine(w.ModsDir, "HcLfDup Renamed"));

        var r = WriteTools.EslFlag(w.Svc, source: "HcLfDup.esp");

        Assert.Contains("ambiguous", r);
        Assert.DoesNotContain("not a houseCARL patch", r);
    }

    // a new worldspace refuses: its landscape is lost when light; and a write into such a patch makes no offer
    [Fact]
    public void APatchWithANewWorldspaceIsRefusedAndIsNotOffered()
    {
        using var w = new RecordsWorld();
        var o = w.Svc.CreateRecordsBatch(new[] { new CreateOp { RecordType = "Worldspace", Editorid = "HcLfWorld" } }, "HcLfWrld", null);
        Assert.True(o.Success, o.Error);
        Assert.DoesNotContain(ToolNames.EslFlag, o.Note ?? "");

        var r = WriteTools.EslFlag(w.Svc, source: "HcLfWrld.esp");

        Assert.Contains("adds a new worldspace", r);
        Assert.False(Read(o.OutputPath).Light);
    }

    // a new interior cell flags, saying what Engine Fixes version holds it
    [Fact]
    public void APatchWithANewInteriorCellFlagsAndNamesEngineFixes()
    {
        using var w = new RecordsWorld();
        var path = NewPatch(w, "HcLfCell", new CreateOp { RecordType = "Cell", Editorid = "HcLfRoom" });

        var r = WriteTools.EslFlag(w.Svc, source: "HcLfCell.esp");

        Assert.Contains("flagged HcLfCell.esp light", r);
        Assert.Contains("Engine Fixes 7.0.13", r);
        Assert.True(Read(path).Light);
    }

    // flagging a light patch again, or clearing a full one, writes nothing
    [Fact]
    public void FlaggingWhatIsAlreadySoWritesNothing()
    {
        using var w = new RecordsWorld();
        var path = NewPatch(w, "HcLfTwice", Kw("HcLfTwiceKw"));
        Assert.Contains("already a full plugin", WriteTools.EslFlag(w.Svc, source: "HcLfTwice.esp", esl: false));
        Assert.Contains("flagged", WriteTools.EslFlag(w.Svc, source: "HcLfTwice.esp"));
        var stamp = File.GetLastWriteTimeUtc(path);

        var r = WriteTools.EslFlag(w.Svc, source: "HcLfTwice.esp");

        Assert.Contains("already light", r);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    // 2,048 ids flag; the 2,049th write refuses with the rebuild; the rebuild keeps every id; the next write lands
    [Fact]
    public void AFullLightPatchRefusesTheNextRecordAndTheRebuildLetsItLand()
    {
        using var w = new RecordsWorld();
        var path = NewPatch(w, "HcLfGrow", Enumerable.Range(0, 2048).Select(i => Kw($"HcLfG{i}")).ToArray());
        var ids = Read(path).Own;
        Assert.Equal(0xFFFu, ids.Max());
        Assert.Contains("flagged HcLfGrow.esp light", WriteTools.EslFlag(w.Svc, source: "HcLfGrow.esp"));
        var before = File.ReadAllBytes(path);

        var over = w.Svc.CreateRecordsBatch(new[] { Kw("HcLfOver") }, null, "HcLfGrow.esp");

        Assert.False(over.Success);
        Assert.StartsWith("houseCARL did not write 'HcLfGrow.esp'", over.Error);   // the whole sentence, no serialize lead before it
        Assert.Contains($"{ToolNames.EslFlag} source=\"HcLfGrow.esp\" esl=false", over.Error);
        Assert.Equal(before, File.ReadAllBytes(path));

        Assert.Contains("rebuilt HcLfGrow.esp as a full plugin", WriteTools.EslFlag(w.Svc, source: "HcLfGrow.esp", esl: false));
        var rebuilt = Read(path);
        Assert.False(rebuilt.Light);
        Assert.Equal(ids.Order(), rebuilt.Own.Order());

        var next = w.Svc.CreateRecordsBatch(new[] { Kw("HcLfOver") }, null, "HcLfGrow.esp");

        Assert.True(next.Success, next.Error);
        Assert.Equal(0x1000u, next.Created.Single().FormKey.ID);
        Assert.DoesNotContain(ToolNames.EslFlag, next.Note ?? "");   // past the window: no offer
    }

    // ---- the offer: each lane's write into a houseCARL patch that is not light and fits ends with it ----

    static void Offered(string? note, string file)
        => Assert.Contains($"{ToolNames.EslFlag} source=\"{file}\" flags it light", note ?? "");

    [Fact]
    public void ACreateIntoAFullPatchThatFitsOffersTheFlag()
    {
        using var w = new RecordsWorld();
        NewPatch(w, "HcLfOffer", Kw("HcLfOfferKw"));

        var o = w.Svc.CreateRecordsBatch(new[] { Kw("HcLfOfferKw2") }, null, "HcLfOffer.esp");

        Assert.True(o.Success, o.Error);
        Offered(o.Note, "HcLfOffer.esp");
    }

    [Fact]
    public void AWriteIntoALightPatchMakesNoOffer()
    {
        using var w = new RecordsWorld();
        NewPatch(w, "HcLfLit", Kw("HcLfLitKw"));
        WriteTools.EslFlag(w.Svc, source: "HcLfLit.esp");

        var o = w.Svc.CreateRecordsBatch(new[] { Kw("HcLfLitKw2") }, null, "HcLfLit.esp");

        Assert.True(o.Success, o.Error);
        Assert.DoesNotContain(ToolNames.EslFlag, o.Note ?? "");
    }

    [Fact]
    public void AnApplyIntoANewPatchOffersTheFlag()
    {
        using var w = new RecordsWorld();
        var o = w.Svc.ApplyEdits(new[] { new BulkOp { Formid = RecordsWorld.Fid(w.Weapons[0]), FieldPath = "BasicStats.Damage", Value = "5" } }, "HcLfApp", null);
        Assert.True(o.Success, o.Error);
        Offered(o.Note, "HcLfApp.esp");
    }

    [Fact]
    public void ADryRunApplyMakesNoOffer()
    {
        using var w = new RecordsWorld();
        NewPatch(w, "HcLfDry", Kw("HcLfDryKw"));
        var o = w.Svc.ApplyEdits(new[] { new BulkOp { Formid = RecordsWorld.Fid(w.Weapons[0]), FieldPath = "BasicStats.Damage", Value = "5" } },
                                 null, "HcLfDry.esp", dryRun: true);
        Assert.True(o.Success, o.Error);
        Assert.DoesNotContain(ToolNames.EslFlag, o.Note ?? "");
    }

    [Fact]
    public void AnInPlaceApplyOnAPluginHouseCarlDoesNotOwnMakesNoOffer()
    {
        using var w = new RecordsWorld();
        var o = w.Svc.ApplyEdits(new[] { new BulkOp { Formid = RecordsWorld.Fid(w.Weapons[0]), FieldPath = "BasicStats.Damage", Value = "5" } },
                                 null, null, target: w.OverrideName, inPlace: true, acknowledge: true);
        Assert.True(o.Success, o.Error);
        Assert.DoesNotContain(ToolNames.EslFlag, o.Note ?? "");
    }

    [Fact]
    public void AForwardIntoANewPatchOffersTheFlag()
    {
        using var w = new RecordsWorld();
        var o = w.Svc.ForwardRecords(new[] { RecordsWorld.Fid(w.Weapons[0]) }, w.MasterName, "HcLfFwd", null);
        Assert.True(o.Success, o.Error);
        Offered(o.Note, "HcLfFwd.esp");
    }

    [Fact]
    public void ARemoveFromAPatchOffersTheFlag()
    {
        using var w = new RecordsWorld();
        var created = w.Svc.CreateRecordsBatch(new[] { Kw("HcLfRmA"), Kw("HcLfRmB") }, "HcLfRm", null);
        Assert.True(created.Success, created.Error);

        var o = w.Svc.RemoveRecords(new[] { FormIdToken.Of(created.Created[0].FormKey) }, "HcLfRm.esp");

        Assert.True(o.Success, o.Error);
        Offered(o.Note, "HcLfRm.esp");
    }

    [Fact]
    public void ACopyIntoANewPatchOffersTheFlag()
    {
        using var w = new CopyServiceWorld();
        var o = w.Copy(w.SrcNpc, new[] { "Src.esp" }, CopyServiceWorld.HeadParts, CopyServiceWorld.NoExclusions, null, "HcLfCopyNpc", "HcLfCopy", null);
        Assert.True(o.Success, o.EngineError);
        Offered(o.Note, "HcLfCopy.esp");
        Assert.Contains("flags it light", CopyTools.Render(o));
    }
}
