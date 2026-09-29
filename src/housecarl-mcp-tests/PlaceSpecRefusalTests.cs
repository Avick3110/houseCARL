using System.Text.Json;
using HousecarlMcp;
using Xunit;
using static HousecarlMcpTests.PlaceInstance;

namespace HousecarlMcpTests;

/// <summary>One instance for the place tool's member checks: a single mod, GMod, supplying the FaceGen head only, and
/// a FaceGen archive on disk outside the mods tree.</summary>
public sealed class PlaceSpecWorld : IDisposable
{
    public PlaceInstance P { get; } = new();
    public LoadOrderService Svc { get; }
    /// <summary>A full '.bsa' path holding both FaceGen files, outside the mods tree.</summary>
    public string Archive { get; }

    public PlaceSpecWorld()
    {
        Loose(P.Mod("GMod"), FacegenRel, new byte[] { 5, 5 });
        P.ProfileWithDummy("GMod", "+GMod");
        Archive = P.Scratch("Face Pair.bsa", FaceArchive());
        Svc = P.Open();
    }

    public void Dispose() => P.Dispose();
}

/// <summary>The place tool's own checks on its members, before anything is resolved: formid and path, kind, the set-level
/// kind= and source_provider=, the both-slots source rule, and the strict wire reader. Nothing here changes what GMod
/// supplies, so the instance is shared.</summary>
[Trait("tier", "integration")]
public sealed class PlaceSpecRefusalTests : IClassFixture<PlaceSpecWorld>
{
    readonly PlaceSpecWorld _w;
    public PlaceSpecRefusalTests(PlaceSpecWorld w) => _w = w;

    // Probe G: "one destination, formid with no kind: NOT refused — it places both FaceGen files". Strengthened: the
    // probe only checked that a retired refusal was absent, which cannot fail; this asserts the two-file expansion.
    [Fact]
    public void AFormidWithNoKindExpandsToBothFaceGenFiles()
    {
        var text = PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Formid = FacegenFormId } }, patch: "BothSlots");

        Assert.Contains("placed 1 of 2 asset(s) (1 failed)", text);
        Assert.Contains("0001A51A.dds", text);
        Assert.NotNull(_w.P.PlacedFileFrom(text, FacegenRel));
    }

    // Probe G: "a member carrying BOTH formid and path is refused" and "a member carrying NEITHER formid nor path is refused".
    [Fact]
    public void AMemberNeedsExactlyOneOfFormidAndPath()
    {
        Assert.Contains("exactly one", PlaceTools.Place(_w.Svc,
            new[] { new PlaceTarget { Formid = FacegenFormId, Path = "meshes/x.nif", Kind = "mesh" } }));
        Assert.Contains("exactly one", PlaceTools.Place(_w.Svc, new[] { new PlaceTarget() }));
    }

    // Probe G: "a malformed formid is refused named".
    [Fact]
    public void AMalformedFormidIsRefused()
    {
        Assert.Contains("bad formid", PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Formid = "not-a-formid", Kind = "mesh" } }));
    }

    // Probe G: "a bad kind token is refused named" and "the set-level kind= reaches a member that names none".
    [Fact]
    public void ABadKindIsRefusedOnTheMemberAndFromTheSetLevel()
    {
        Assert.Contains("not valid", PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Formid = FacegenFormId, Kind = "bogus" } }));
        Assert.Contains("not valid", PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Formid = FacegenFormId } }, kind: "bogus"));
    }

    // Probe G: "a bad set-level kind= is refused under its own name, on a set of path= members that ignore it".
    [Fact]
    public void ABadSetLevelKindIsRefusedUnderItsOwnNameOnAPathOnlySet()
    {
        var text = PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Path = "meshes/x.nif" } }, kind: "bogus");

        Assert.Contains("not valid", text);
        Assert.DoesNotContain("assets[0]", text);
    }

    // Probe G: "a call-level source_provider= does not attach to an on-disk source= member" and "...and the row says
    // the set-level pole was not applied to it".
    [Fact]
    public void ASetLevelPoleIsWithheldFromAnOnDiskSourceAndTheRowSaysSo()
    {
        var fan = PlaceTools.Place(_w.Svc,
            new[] { new PlaceTarget { Path = @"meshes\hcprobe\fan.nif", Source = @"C:\nope\fan.nif" } }, source_provider: "SomeMod");

        Assert.DoesNotContain(WriteSentences.PlaceSourceProviderNeedsRelPath, fan);
        Assert.Contains("set-level source_provider not applied", fan);
    }

    // Probe G: "...and a member's own source_provider= against an on-disk source is still refused".
    [Fact]
    public void AMembersOwnPoleOnAnOnDiskSourceIsRefused()
    {
        var own = PlaceTools.Place(_w.Svc, new[]
            { new PlaceTarget { Path = @"meshes\hcprobe\fan.nif", Source = @"C:\nope\fan.nif", SourceProvider = "SomeMod" } });

        Assert.Contains(WriteSentences.PlaceSourceProviderNeedsRelPath, own);
    }

    // Probe G: "...and a forward-slash destination still says it".
    [Fact]
    public void AForwardSlashDestinationStillSaysThePoleWasWithheld()
    {
        var slash = PlaceTools.Place(_w.Svc,
            new[] { new PlaceTarget { Path = "meshes/hcprobe/fan.nif", Source = @"C:\nope\fan.nif" } }, source_provider: "SomeMod");

        Assert.Contains("set-level source_provider not applied", slash);
    }

    // Probe G: "an undeclared assets= member is refused by name, never dropped". Strengthened: the member's name is
    // matched quoted, as the refusal writes it, rather than the bare word "provider".
    [Fact]
    public void AnUndeclaredMemberOnTheWireIsRefusedByName()
    {
        using var doc = JsonDocument.Parse("""[ { "path": "meshes/x.nif", "provider": "SomeMod" } ]""");

        var strict = PlaceTools.Place(_w.Svc, doc.RootElement);

        Assert.Contains("'provider'", strict);
        Assert.Contains("assets could not be parsed", strict);
    }

    // Probe G: "place: a both-expansion (formid, no kind) with a non-.bsa source is refused".
    [Fact]
    public void ABothSlotsMemberWithALooseSourceIsRefused()
    {
        var text = PlaceTools.Place(_w.Svc, new[] { new PlaceTarget { Formid = FacegenFormId, Source = @"C:\loose.nif" } });

        Assert.Contains("must be a FULL '.bsa' path", text);
        Assert.DoesNotContain("mod folder:", text);
    }

    // Probe G: "place: a both-expansion with a RELATIVE '.bsa' source is refused (one VFS path cannot serve two slots)".
    [Fact]
    public void ABothSlotsMemberWithARelativeBsaSourceIsRefused()
    {
        Assert.Contains("must be a FULL '.bsa' path", PlaceTools.Place(_w.Svc,
            new[] { new PlaceTarget { Formid = FacegenFormId, Source = @"meshes\some\thing.bsa" } }));
    }

    // Probe G: "place: the both-expansion refusal states when source_provider= actually applies there".
    [Fact]
    public void TheBothSlotsRefusalSaysWhenAPoleApplies()
    {
        Assert.Contains(WriteSentences.PlaceBothSlotsPoleConstraint, PlaceTools.Place(_w.Svc,
            new[] { new PlaceTarget { Formid = FacegenFormId, Source = @"C:\nope\x.nif", SourceProvider = "GMod" } }));
    }

    // Probe G: "place: a QUOTED .bsa source in a both-expansion is ACCEPTED (not refused for the trailing quote)".
    // Strengthened: the probe checked a refusal wording that no longer exists, which cannot fail; this asserts both
    // slots placed out of the quoted archive.
    [Fact]
    public void AQuotedFullBsaSourceServesBothSlots()
    {
        var text = PlaceTools.Place(_w.Svc,
            new[] { new PlaceTarget { Formid = FacegenFormId, Source = "\"" + _w.Archive + "\"" } }, patch: "QuotedPair");

        Assert.Contains("placed 2 of 2", text);
        Assert.Equal(ArchiveTintBytes, File.ReadAllBytes(_w.P.PlacedFileFrom(text, FacegenTintRel)!));
    }

    // Probe G: "place: an empty assets array is rejected".
    [Fact]
    public void AnEmptySetIsRefused()
    {
        Assert.Contains("empty", PlaceTools.Place(_w.Svc, Array.Empty<PlaceTarget>()));
    }

    // Probe L: "the wire array deserializes", "source_provider arrives under its wire name", "...and so do the sibling
    // fields this spec pairs it with".
    [Fact]
    public void TheWireFieldNamesLandOnTheirMembers()
    {
        var specs = JsonSerializer.Deserialize<PlaceTarget[]>(
            """[ { "path": "meshes\\x.nif", "source": "meshes\\y.nif", "source_provider": "SomeMod" } ]""");

        var only = Assert.Single(specs!);
        Assert.Equal("SomeMod", only.SourceProvider);
        Assert.Equal(@"meshes\y.nif", only.Source);
        Assert.Equal(@"meshes\x.nif", only.Path);
    }
}
