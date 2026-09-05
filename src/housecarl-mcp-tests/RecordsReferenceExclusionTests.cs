using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The negated <c>references=</c> entry (a leading '!') and the quantified <c>where=</c> step, driven
/// end to end through the tool over a real scan.</summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsReferenceExclusionTests : RecordsTestBase
{
    public RecordsReferenceExclusionTests(RecordsFixture f) : base(f) { }

    [Fact]
    public void ANegatedReferenceKeepsOnlyTheRecordsThatDoNotLinkThatTarget()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, references: new[] { "!" + Fid(W.MgefA) });
        Served(r, "HcRecSpellB");
        Assert.DoesNotContain("HcRecSpellA", r);
        Assert.DoesNotContain("HcRecSpellC", r);
    }

    [Fact]
    public void APlainAndANegatedTargetInOneCallComposeByAnd()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" },
                                     references: new[] { Fid(W.MgefA), "!" + Fid(W.MgefB) });
        Served(r, "HcRecSpellA", "HcRecSpellC");
        Assert.DoesNotContain("HcRecSpellB", r);
    }

    [Fact]
    public void ANegatedReferenceStillNeedsABoundingScope() =>
        Refused(RecordsTools.Records(Svc, references: new[] { "!" + Fid(W.MgefA) }), "types=");

    [Fact]
    public void ABareBangNamesNoTargetAndIsRefused() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "SPEL" }, references: new[] { "!" }), "names no target");

    [Fact]
    public void TheQuantifiedStepRidesWhereEndToEnd()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" },
                                     where: new[] { "Effects[*any].Data.Magnitude > 8" });
        Served(r, "HcRecSpellC");
        Assert.DoesNotContain("HcRecSpellA", r);
    }

    [Fact]
    public void TheBareStarIsRefusedThroughTheToolNamingTheFoldTokens()
    {
        // The scan's parse refusal rides under the header line, so this is not the bare "error:" shape.
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, where: new[] { "Effects[*].Data.Magnitude > 8" });
        Assert.Contains("error:", r);
        Assert.Contains("[*any]", r);
    }
}
