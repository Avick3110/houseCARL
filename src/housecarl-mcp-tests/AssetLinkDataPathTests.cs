using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A path as a file names it, through the one door to the Data path the game opens: an ABSENT
/// <c>data\textures\…</c> or <c>data\meshes\…</c> path gets the verified hint naming its Data form, and nif_inspect's
/// paths section shows each slot's Data path and winner, the same answer a direct asset_status call gives.</summary>
[Trait("tier", "integration")]
public sealed class AssetLinkDataPathTests : IDisposable
{
    const string Tint = @"textures\actors\character\facegendata\facetint\guard.esp\00000001.dds";
    const string Head = @"textures\Actors\Character\Female\FemaleHead.dds";
    const string Msn = @"textures\actors\character\female\femalehead_msn.dds";
    const string Mesh = @"meshes\actors\guard\head.nif";

    // The three forms one FaceGen NIF carries, an absent slot and one no Data path can be made of.
    static readonly string[] Slots =
    {
        @"Actors\Character\Female\FemaleHead.dds", Msn, "", "", "", "",
        @"data\" + Tint, @"textures\nowhere\ghost.dds", @"..\escape.dds",
    };

    readonly PlaceInstance _p = new();
    readonly LoadOrderService _svc;

    public AssetLinkDataPathTests()
    {
        var mod = _p.Mod("GuardFace");
        foreach (var rel in new[] { Tint, Head, Msn }) PlaceInstance.Loose(mod, rel, "x"u8.ToArray());
        PlaceInstance.Loose(mod, Mesh, NifSetMeshes.Textured(Slots));
        _p.Profile(Array.Empty<string>(), Array.Empty<string>(), new[] { "+GuardFace" });
        _svc = _p.Open();
    }

    public void Dispose() => _p.Dispose();

    [Fact]
    public void ADataPrefixedTexturePathSuggestsItsDataForm()
    {
        var s = _svc.AssetStatus(new[] { @"data\" + Tint }).Results[0];

        Assert.False(s.Hit!.Exists);
        Assert.Equal(new[] { Tint }, s.PrefixSuggestions);
    }

    [Fact]
    public void ADataPrefixedPathWhoseDataFormAlsoMissesSuggestsNothing()
    {
        var s = _svc.AssetStatus(new[] { @"data\textures\nowhere\ghost.dds" }).Results[0];

        Assert.False(s.Hit!.Exists);
        Assert.Empty(s.PrefixSuggestions ?? Array.Empty<string>());
    }

    [Fact]
    public void TheRenderedHintSaysDataIsDroppedNotThatARootIsMissing()
    {
        var rendered = AssetWire.Render(_svc.AssetStatus(new[] { @"data\" + Tint }), 20000);

        Assert.Contains("did you mean `" + Tint + "`", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AssetPathHint.DataPrefixSentence, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("relative to its root folder", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaceNamesTheDataFormAndWhyOnADataPrefixedPath()
    {
        var o = _svc.PlaceAssets(new[] { new PlaceRequest(@"data\" + Tint, null) }, null, null).Results[0];

        Assert.False(o.Placed);
        Assert.Contains("Did you mean `" + Tint + "`? " + AssetPathHint.DataPrefixSentence, o.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADataPrefixedMeshPathGetsTheMeshHint()
    {
        var r = _svc.NifInspect(new[] { @"data\" + Mesh }, null).Results[0];

        Assert.True(r.Absent);
        Assert.Contains("Did you mean `" + Mesh + "`? " + AssetPathHint.DataPrefixSentence, r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ADataPrefixedMeshPathThatMissesNamesItsDataForm()
    {
        var r = _svc.NifInspect(new[] { @"data\meshes\nowhere\ghost.nif" }, null).Results[0];

        Assert.DoesNotContain("Did you mean", r.Error, StringComparison.Ordinal);
        Assert.Contains(@"`meshes\nowhere\ghost.nif`", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Model.File", r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void EachSlotResolvesAsADirectAssetStatusCallOnItsDataPath()
    {
        var links = _svc.NifInspect(new[] { Mesh }, null).Results[0].SlotLinks!;

        foreach (var (raw, data, exists) in new[]
        {
            (Slots[0], Head, true), (Msn, Msn, true), (Slots[6], Tint, true),
            (Slots[7], @"textures\nowhere\ghost.dds", false),
        })
        {
            var link = links[raw];
            Assert.Equal(data, link.DataPath, StringComparer.OrdinalIgnoreCase);
            var direct = _svc.AssetStatus(new[] { data }).Results[0].Hit!;
            Assert.Equal(exists, link.Hit!.Exists);
            Assert.Equal(direct.Exists, link.Hit.Exists);
            Assert.Equal(direct.Winner?.Source, link.Hit.Winner?.Source);
            Assert.Equal(direct.Winner?.Kind, link.Hit.Winner?.Kind);
        }
    }

    [Fact]
    public void ASlotNoDataPathCanBeMadeOfIsItsOwnError()
    {
        var link = _svc.NifInspect(new[] { Mesh }, null).Results[0].SlotLinks![Slots[8]];

        Assert.Null(link.DataPath);
        Assert.Contains("..", link.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePathsSectionPrintsTheRawSlotAndItsDataPathAndWinner()
    {
        var text = NifWire.Render(_svc.NifInspect(new[] { Mesh }, null),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "paths" }, Array.Empty<string>(), 80_000);

        Assert.Contains("tex[6] (TintMask): data\\" + Tint + "\n      data path: " + Tint + "  WINS: \"GuardFace\" (loose)", text, StringComparison.Ordinal);
        Assert.Contains("data path: textures\\nowhere\\ghost.dds  ABSENT", text, StringComparison.Ordinal);
        Assert.Contains("data path: none — ", text, StringComparison.Ordinal);
    }
}
