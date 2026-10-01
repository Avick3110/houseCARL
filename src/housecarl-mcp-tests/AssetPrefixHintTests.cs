using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The missing-root suggestion on an absent asset path (#273, #283): a record-relative path whose meshes\ or
/// textures\ form really is provided gets a "did you mean" naming that form, across nif_inspect, nif_set,
/// asset_status and place; a path whose prefixed form also misses, an already-rooted path, a non-asset root and a
/// hit get none. The instance holds meshes\actors\canine\wolf.nif and textures\actors\canine\wolf.dds, loose.</summary>
[Trait("tier", "integration")]
public sealed class AssetPrefixHintTests : IDisposable
{
    const string MeshRel = @"meshes\actors\canine\wolf.nif";
    const string RecordRel = @"actors\canine\wolf.nif";
    const string TexRel = @"textures\actors\canine\wolf.dds";
    const string TexRecordRel = @"actors\canine\wolf.dds";
    const string Ghost = @"nowhere\ghost.nif";
    const string Sound = @"sound\fx\ghost.wav";

    readonly PlaceInstance _p = new();
    readonly LoadOrderService _svc;

    public AssetPrefixHintTests()
    {
        var mod = _p.Mod("WolfMod");
        PlaceInstance.Loose(mod, MeshRel, "x"u8.ToArray());
        PlaceInstance.Loose(mod, TexRel, "x"u8.ToArray());
        _p.Profile(Array.Empty<string>(), Array.Empty<string>(), new[] { "+WolfMod" });
        _svc = _p.Open();
    }

    public void Dispose() => _p.Dispose();

    static string DidYouMean(string rel) => "Did you mean `" + rel + "`";

    // Probe: "FIRES — the record-relative path is ABSENT, and the answer names the meshes\-prefixed path that IS provided".
    [Fact]
    public void NifInspectOnARecordRelativePathNamesTheProvidedMeshesForm()
    {
        var r = _svc.NifInspect(new[] { RecordRel }, null).Results[0];

        Assert.True(r.Absent);
        Assert.Contains(DidYouMean(MeshRel), r.Error, StringComparison.OrdinalIgnoreCase);
    }

    // Probe: "NOT-A-GUESS — a record-relative path whose prefixed form ALSO misses gets no 'did you mean', only the
    // convention note (which claims no file)".
    [Fact]
    public void NifInspectGivesNoSuggestionWhenThePrefixedFormAlsoMisses()
    {
        var r = _svc.NifInspect(new[] { Ghost }, null).Results[0];

        Assert.True(r.Absent);
        Assert.DoesNotContain("Did you mean", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"`meshes\nowhere\ghost.nif`", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not provided", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    // Probe: "NO-NOISE — a path already under meshes\ that is simply absent gets no prefix hint at all".
    [Fact]
    public void NifInspectGivesNoHintForAnAbsentPathAlreadyUnderMeshes()
    {
        var r = _svc.NifInspect(new[] { @"meshes\nowhere\ghost.nif" }, null).Results[0];

        Assert.True(r.Absent);
        Assert.NotNull(r.Error);
        Assert.DoesNotContain("Did you mean", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stored relative to", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    // Probe: "CONTROL — the real Data-relative path still resolves (the hint path never touches a hit)".
    [Fact]
    public void NifInspectStillResolvesTheRealPath()
    {
        var r = _svc.NifInspect(new[] { MeshRel }, null).Results[0];

        Assert.False(r.Absent, r.Error);
        Assert.Single(r.Providers);
    }

    // Probe: "NIF-SET — the same verified suggestion rides the nif_set ABSENT refusal".
    [Fact]
    public void NifSetCarriesTheSameSuggestion()
    {
        var set = _svc.NifSet(RecordRel, new[] { new NifSetOp(NifSetOpKind.RenameShape, "Old", NewName: "New") },
            null, null, null, false, false);

        Assert.Contains(DidYouMean(MeshRel), set.Error, StringComparison.OrdinalIgnoreCase);
    }

    // Probe: "ASSET-MESH — a record-relative mesh path suggests its meshes\ form" and "ASSET-TEX — a record-relative
    // texture path suggests its textures\ form".
    [Theory]
    [InlineData(RecordRel, MeshRel)]
    [InlineData(TexRecordRel, TexRel)]
    public void AssetStatusSuggestsTheRootThatIsProvided(string asked, string suggested)
    {
        var s = _svc.AssetStatus(new[] { asked }).Results[0];

        Assert.False(s.Hit!.Exists);
        Assert.Equal(new[] { suggested }, s.PrefixSuggestions);
    }

    // Probe: "ASSET-QUIET — a genuinely absent path suggests nothing", "…a non-asset-root path (sound\) gets no meshes\
    // lecture" and "…a path that RESOLVES carries no suggestion".
    [Theory]
    [InlineData(Ghost, false)]
    [InlineData(Sound, false)]
    [InlineData(MeshRel, true)]
    public void AssetStatusSuggestsNothingWithoutAVerifiedHit(string asked, bool exists)
    {
        var s = _svc.AssetStatus(new[] { asked }).Results[0];

        Assert.Equal(exists, s.Hit!.Exists);
        Assert.Empty(s.PrefixSuggestions ?? Array.Empty<string>());
    }

    // Probe: "RENDER — the suggestion reaches the rendered asset_status output, not just the data record".
    [Fact]
    public void TheSuggestionReachesTheRenderedAssetStatus()
    {
        var rendered = AssetWire.Render(_svc.AssetStatus(new[] { RecordRel }), 20000);

        Assert.Contains("did you mean", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(MeshRel, rendered, StringComparison.OrdinalIgnoreCase);
    }

    // Probe: "PLACE — the auto-resolve refusal names the meshes\-prefixed copy that IS provided", "PLACE-ORDER — the
    // hint precedes the 'Pass source=' fallback" and "PLACE-TEX — both roots are tried here too".
    [Theory]
    [InlineData(RecordRel, MeshRel)]
    [InlineData(TexRecordRel, TexRel)]
    public void PlaceNamesTheProvidedRootBeforeTheSourceFallback(string asked, string suggested)
    {
        var o = _svc.PlaceAssets(new[] { new PlaceRequest(asked, null) }, null, null).Results[0];

        Assert.False(o.Placed);
        int iHint = o.Error!.IndexOf(DidYouMean(suggested), StringComparison.OrdinalIgnoreCase);
        int iSrc = o.Error.IndexOf("Pass source=", StringComparison.OrdinalIgnoreCase);
        Assert.True(iHint >= 0 && iSrc > iHint, o.Error);
    }

    // Probe: "PLACE-QUIET — a path whose prefixed form ALSO misses gets no 'did you mean'" and "…a non-asset-root path
    // (sound\) gets no meshes\ lecture".
    [Theory]
    [InlineData(Ghost)]
    [InlineData(Sound)]
    public void PlaceSuggestsNothingWithoutAVerifiedHit(string asked)
    {
        var o = _svc.PlaceAssets(new[] { new PlaceRequest(asked, null) }, null, null).Results[0];

        Assert.False(o.Placed);
        Assert.NotNull(o.Error);
        Assert.DoesNotContain("Did you mean", o.Error, StringComparison.OrdinalIgnoreCase);
    }

    // Probe: "PLACE-SOURCE — an explicit source= still places at a path nothing provides (the hint never fires on that arm)".
    [Fact]
    public void AnExplicitSourcePlacesAtAPathNothingProvides()
    {
        var src = _p.Scratch("brand-new.nif", "y"u8.ToArray());

        var o = _svc.PlaceAssets(new[] { new PlaceRequest(RecordRel, src) }, null, null).Results[0];

        Assert.True(o.Placed, o.Error);
        Assert.Null(o.Error);
    }
}
