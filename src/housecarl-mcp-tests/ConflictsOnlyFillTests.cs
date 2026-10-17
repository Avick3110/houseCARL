using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The conflicts-only scan's summaries are filled through the read door.</summary>
[Trait("tier", "integration")]
public sealed class ConflictsOnlyFillTests : IClassFixture<SmallDoorWorld>
{
    readonly SmallDoorWorld _w;
    public ConflictsOnlyFillTests(SmallDoorWorld w) => _w = w;

    /// <summary>The three weapons Top wins are summarised off one walk of Top's Weapons group, not one read per row.</summary>
    [Fact]
    public void AConflictsOnlyRenderReadsEachWinnerGroupOncePerOutcome()
    {
        var d = GroupReadDelta.Of(_w.Svc, () => RecordsTools.Records(_w.Svc, conflicts_only: true), "HcSdWeapon1Top", "HcSdWeapon3Top");

        Assert.Equal(new Dictionary<(string, string), int> { [(_w.Top.FileName.String, "Weapons")] = 1 }, d);
    }
}
