using HousecarlCore;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The merge refusals, each named with its fix (the former merge-service-guard arm REFUSE).</summary>
[Trait("tier", "integration")]
[Collection("merge-service")]
public sealed class MergeServiceRefusalTests
{
    readonly MergeServiceWorld _w;
    public MergeServiceRefusalTests(MergeServiceWorld w) => _w = w;

    string Refused(IReadOnlyList<string>? donors, string output)
    {
        var r = _w.Svc.MergePlugins(donors, output);
        Assert.False(r.Success);
        Assert.NotNull(r.Error);
        return r.Error!;
    }

    // REFUSE zero donors, remedy names both shapes
    [Fact]
    public void ZeroDonorsIsRefusedNamingBothShapes()
    {
        var e = Refused(Array.Empty<string>(), "HcMgX.esp");
        Assert.Contains("at least ONE", e, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rename", e, StringComparison.OrdinalIgnoreCase);
    }

    // REFUSE blank-only donor list
    [Fact]
    public void ABlankOnlyDonorListIsRefused()
        => Assert.Contains("at least ONE", Refused(new[] { "  ", "" }, "HcMgX.esp"), StringComparison.OrdinalIgnoreCase);

    // REFUSE null donor list
    [Fact]
    public void ANullDonorListIsRefused()
        => Assert.Contains("at least ONE", Refused(null, "HcMgX.esp"), StringComparison.OrdinalIgnoreCase);

    // REFUSE unknown donor
    [Fact]
    public void AnUnknownDonorIsRefused()
        => Assert.Contains("not an active plugin", Refused(new[] { "HcMgA.esp", "HcMgNope.esp" }, "HcMgX.esp"), StringComparison.OrdinalIgnoreCase);

    // REFUSE output already in the load order
    [Fact]
    public void AnOutputAlreadyInTheLoadOrderIsRefused()
        => Assert.Contains("already an active plugin", Refused(new[] { "HcMgA.esp", "HcMgB.esp" }, "HcMgDep.esp"), StringComparison.OrdinalIgnoreCase);

    // REFUSE output == donor
    [Fact]
    public void AnOutputThatIsADonorIsRefused()
        => Assert.Contains("cannot also be a donor", Refused(new[] { "HcMgA.esp", "HcMgB.esp" }, "HcMgA.esp"), StringComparison.OrdinalIgnoreCase);

    // REFUSE .esl output with the compact-after remedy
    // …and its reason is the claim that holds on every path
    [Fact]
    public void AnEslOutputIsRefusedWithTheCompactRemedyAndTheTrueReason()
    {
        var e = Refused(new[] { "HcMgA.esp", "HcMgB.esp" }, "HcMgLight.esl");
        Assert.Contains(".esl", e, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compact", e, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never constrains object ids to the light window", e);
        Assert.DoesNotContain("keeps each donor's object ids", e);
        Assert.DoesNotContain("where they already are", e);
    }
}
