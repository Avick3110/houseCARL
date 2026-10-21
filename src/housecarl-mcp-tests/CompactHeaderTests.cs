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
        m.ModHeader.OverriddenForms = new() { CompactServiceWorld.BaseCell };
        m.ModHeader.INTV = 7;
        m.ModHeader.INCC = 3;
        var dir = Path.Combine(w.Mods, "HeadMod"); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, HeadKey.FileName.String);
        using var baseOv = SkyrimMod.CreateFromBinaryOverlay(
            Path.Combine(w.Mods, "BaseMod", CompactServiceWorld.BaseKey.FileName.String), SkyrimRelease.SkyrimSE);
        m.BeginWrite.ToPath(path).WithLoadOrder(new[] { baseOv }).Write();
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
        Assert.Equal(CompactServiceWorld.BaseCell, Assert.Single(h.OverriddenForms!).FormKey);
        Assert.Equal(7, h.INTV);
        Assert.Equal(3, h.INCC);
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

        var again = w.Svc.CompactPlugin("HcCsLib.esp", repointExternals: true, inPlace: true, acknowledge: true);

        Assert.True(again.Success, again.Error);
        Assert.Equal(0, again.RecordsMoved);
        var text = WriteTools.RenderCompact(again);
        Assert.DoesNotContain("records renumbered", text);
        Assert.DoesNotContain("record renumbered", text);
        Assert.DoesNotContain("1/1 succeeded", text);
        Assert.Contains(WriteSentences.CompactMovedNothing, text);
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
