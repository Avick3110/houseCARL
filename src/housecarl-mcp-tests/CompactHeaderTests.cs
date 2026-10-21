using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A compaction keeps the source's header (#1215), and a compaction whose map moves nothing says so (#1205).</summary>
[Trait("tier", "integration")]
public sealed class CompactHeaderTests
{
    static readonly ModKey HeadKey = new("HcCsHead", ModType.Plugin);

    /// <summary>An ESM-flagged plugin with an author, a description, an ONAM entry, INTV and INCC, in an unlisted mod folder.</summary>
    static string WriteHeaderedMod(CompactServiceWorld w)
    {
        var m = new SkyrimMod(HeadKey, SkyrimRelease.SkyrimSE);
        m.Weapons.Add(new Weapon(new FormKey(HeadKey, 0x1A01), SkyrimRelease.SkyrimSE) { EditorID = "HcCsHeadWeap" });
        // A record naming Base makes it a master, which the ONAM entry needs to be written.
        var fl = new FormList(new FormKey(HeadKey, 0x1A02), SkyrimRelease.SkyrimSE) { EditorID = "HcCsHeadList" };
        fl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(CompactServiceWorld.BaseCell));
        m.FormLists.Add(fl);
        m.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
        m.ModHeader.Author = "Hc Author";
        m.ModHeader.Description = "Hc description";
        // ONAM names a master record and one of the plugin's own, which the renumber must map.
        m.ModHeader.OverriddenForms = new() { CompactServiceWorld.BaseCell, new FormKey(HeadKey, 0x1A01) };
        m.ModHeader.Stats.Version = 1.7f;
        m.ModHeader.Stats.NextFormID = 0x1A06;
        m.ModHeader.INTV = 7;
        m.ModHeader.INCC = 3;
        var dir = Path.Combine(w.Mods, "HeadMod"); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, HeadKey.FileName.String);
        using var baseOv = SkyrimMod.CreateFromBinaryOverlay(
            Path.Combine(w.Mods, "BaseMod", CompactServiceWorld.BaseKey.FileName.String), SkyrimRelease.SkyrimSE);
        m.BeginWrite.ToPath(path).WithLoadOrder(new[] { baseOv }).NoNextFormIDProcessing().Write();
        return path;
    }

    static void AssertHeaderKept(string path)
    {
        using var pp = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var h = pp.ModHeader;
        Assert.True(h.Flags.HasFlag(SkyrimModHeader.HeaderFlag.Master), $"flags 0x{(uint)h.Flags:X}");
        Assert.True(pp.IsSmallMaster);
        Assert.Equal("Hc Author", h.Author);
        Assert.Equal("Hc description", h.Description);
        Assert.Equal(new[] { CompactServiceWorld.BaseCell, new FormKey(HeadKey, 0x800) }.OrderBy(k => k.ToString()),
                     h.OverriddenForms!.Select(l => l.FormKey).OrderBy(k => k.ToString()));
        Assert.Equal(7, h.INTV);
        Assert.Equal(3, h.INCC);
        Assert.Equal(1.7f, h.Stats.Version);
        // The source's NextObjectID is above the window; the compaction's own is the floor plus its two records.
        Assert.Equal(0x802u, h.Stats.NextFormID);
    }

    [Fact]
    public void ANewPluginCompactionKeepsTheMasterFlagAuthorDescriptionAndOverriddenForms()
    {
        using var w = new CompactServiceWorld();
        WriteHeaderedMod(w);

        var o = w.Svc.CompactPlugin(HeadKey.FileName.String);

        Assert.True(o.Success, o.Error);
        AssertHeaderKept(o.OutputPath);
    }

    [Fact]
    public void AnInPlaceCompactionKeepsTheMasterFlagAuthorDescriptionAndOverriddenForms()
    {
        using var w = new CompactServiceWorld();
        var path = WriteHeaderedMod(w);

        var o = w.Svc.CompactPlugin(HeadKey.FileName.String, inPlace: true, acknowledge: true);

        Assert.True(o.Success, o.Error);
        Assert.Equal(path, o.OutputPath);
        AssertHeaderKept(path);
    }

    // A second in-place compaction finds every record already at its compacted id: nothing moves, and the report says so.
    [Fact]
    public void ACompactionWhoseMapMovesNothingDoesNotReportARenumberOrACleanRepoint()
    {
        using var w = new CompactServiceWorld();
        var first = w.Svc.CompactPlugin("HcCsLib.esp", repointExternals: true, inPlace: true, acknowledge: true);
        Assert.True(first.Success, first.Error);
        Assert.Equal(1, first.RecordsMoved);
        var firstText = WriteTools.RenderCompact(first);
        Assert.Contains("1 originating record renumbered into the light range", firstText);
        Assert.Contains("external referencers repointed in place: 1/1 succeeded", firstText);

        var again = w.Svc.CompactPlugin("HcCsLib.esp", repointExternals: true, inPlace: true, acknowledge: true);

        Assert.True(again.Success, again.Error);
        Assert.Equal(0, again.RecordsMoved);
        var text = WriteTools.RenderCompact(again);
        Assert.DoesNotContain("records renumbered", text);
        Assert.DoesNotContain("record renumbered", text);
        Assert.DoesNotContain("1/1 succeeded", text);
        Assert.DoesNotContain("clean compaction", text);
        Assert.Contains("external referencers: none to repoint, since no id moved.\n", text);
        Assert.Contains(WriteSentences.CompactMovedNothing, text);
    }

    [Fact]
    public void ANotLightRunThatMovesNothingDoesNotLeadWithRenumbered()
    {
        using var w = new CompactServiceWorld();
        Assert.True(w.Svc.CompactPlugin("HcCsLib.esp", repointExternals: true, inPlace: true, acknowledge: true).Success);

        var again = w.Svc.CompactPlugin("HcCsLib.esp", esl: false, inPlace: true, acknowledge: true);

        Assert.True(again.Success, again.Error);
        var text = WriteTools.RenderCompact(again);
        Assert.Contains("not light-flagged.\n" + WriteSentences.CompactMovedNothing, text);
        Assert.DoesNotContain("renumbered (not light-flagged)", text);
    }

    // After a repointed compaction, Dep links Lib's record at its compacted id: a re-run moves nothing, so nothing references a move.
    [Fact]
    public void ARunThatMovesNothingNeitherRefusesNorRewritesAReferencer()
    {
        using var w = new CompactServiceWorld();
        Assert.True(w.Svc.CompactPlugin("HcCsLib.esp", repointExternals: true, inPlace: true, acknowledge: true).Success);
        var depPath = Path.Combine(w.Mods, "DepMod", CompactServiceWorld.DepKey.FileName.String);
        var depBefore = File.ReadAllBytes(depPath);

        var again = w.Svc.CompactPlugin("HcCsLib.esp", inPlace: true, acknowledge: true);

        Assert.True(again.Success, again.Error);
        Assert.Empty(again.ExternalPlugins);
        Assert.Empty(again.Repointed);
        Assert.Equal(depBefore, File.ReadAllBytes(depPath));
    }

    // Lib holds 0x800 (already at its compacted id) and 0x1A01 (moves): Dep links the moved one, Over the unmoved one.
    [Fact]
    public void APartialMoveRepointsOnlyAReferencerOfAMovedRecord()
    {
        using var w = new CompactServiceWorld();
        var stays = new FormKey(CompactServiceWorld.LibKey, 0x800);
        w.WriteMod("LibMod", CompactServiceWorld.LibKey, m =>
        {
            m.Weapons.Add(new Weapon(stays, SkyrimRelease.SkyrimSE) { EditorID = "HcCsLibStays" });
            m.Weapons.Add(new Weapon(CompactServiceWorld.LibWeapon, SkyrimRelease.SkyrimSE) { EditorID = "HcCsLibWeap" });
        });
        var overPath = Path.Combine(w.Mods, "OverMod", CompactServiceWorld.OverKey.FileName.String);
        using (var libOv = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(w.Mods, "LibMod", CompactServiceWorld.LibKey.FileName.String), SkyrimRelease.SkyrimSE))
        {
            var o = new SkyrimMod(CompactServiceWorld.OverKey, SkyrimRelease.SkyrimSE);
            var fl = new FormList(new FormKey(CompactServiceWorld.OverKey, 0xA01), SkyrimRelease.SkyrimSE) { EditorID = "HcCsOverList" };
            fl.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(stays));
            o.FormLists.Add(fl);
            o.BeginWrite.ToPath(overPath).WithLoadOrder(new[] { libOv }).Write();
        }
        var overBefore = File.ReadAllBytes(overPath);

        var c = w.Svc.CompactPlugin("HcCsLib.esp", repointExternals: true, inPlace: true, acknowledge: true);

        Assert.True(c.Success, c.Error);
        Assert.Equal(1, c.RecordsMoved);
        Assert.Equal("HcCsDep.esp", Assert.Single(c.ExternalPlugins), ignoreCase: true);
        Assert.True(Assert.Single(c.Repointed).Success);
        Assert.Equal(overBefore, File.ReadAllBytes(overPath));
        var text = WriteTools.RenderCompact(c);
        Assert.Contains("yes — 1 originating record renumbered into the light range 0x800–0xFFF.\n", text);
        Assert.Contains("external referencers repointed in place: 1/1 succeeded\n  OK   HcCsDep.esp\n", text);
        Assert.DoesNotContain(WriteSentences.CompactMovedNothing, text);
    }

    [Fact]
    public void TheReferencerRefusalSaysTheLinksMayResolveToOtherRecords()
    {
        using var w = new CompactServiceWorld();
        var o = w.Svc.CompactPlugin("HcCsLib.esp");

        Assert.False(o.Success);
        Assert.DoesNotContain("no longer exist", o.Error);
        Assert.Contains("a different record", o.Error);
    }
}
