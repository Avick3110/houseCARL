using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The conflicts-only fill lives as long as its outcome, not the process.</summary>
[Trait("tier", "integration")]
public sealed class ConflictsOnlyFillLifetimeTests
{
    /// <summary>Rewriting the overriding plugin with the same FormKeys and new EditorIDs shows the new EditorIDs on the next render.</summary>
    [Fact]
    public void ARenderAfterTheWinnerChangesOnDiskShowsTheNewEditorId()
    {
        using var w = new SmallDoorWorld();
        Assert.Contains("HcSdWeapon1Top", RecordsTools.Records(w.Svc, conflicts_only: true));

        w.RewriteTop("Renamed");
        var after = RecordsTools.Records(w.Svc, conflicts_only: true);

        Assert.Contains("HcSdWeapon1Renamed", after);
        Assert.DoesNotContain("HcSdWeapon1Top", after);
    }
}
