using Xunit;
using static HousecarlMcpTests.NifSetMeshes;

namespace HousecarlMcpTests;

/// <summary>The two write-verification gates fed a bad write directly, so each is proven to catch one rather than merely
/// pass: gate 1 (block content) a collateral change, gate 2 (read-back) a no-op. Migrated from the nif-set-guard probe.</summary>
[Trait("tier", "unit")]
public sealed class NifSetVerifyGateTests
{
    readonly byte[] _guard = Guard();

    // probe: "gate 1 REFUSES a collateral (non-expected-block) change"
    [Fact]
    public void BlockContentGateRefusesACollateralChange()
    {
        var (edited, shapeIdx, _) = TwoBlockEdit(_guard);
        Assert.Contains("should not have touched",
            NifService.VerifyBlockContent(_guard, edited, new HashSet<int> { shapeIdx }, expectHeader: false));
    }

    // probe: "gate 1 PASSES when both changed blocks are expected"
    [Fact]
    public void BlockContentGatePassesWhenBothChangedBlocksAreExpected()
    {
        var (edited, shapeIdx, childIdx) = TwoBlockEdit(_guard);
        Assert.Null(NifService.VerifyBlockContent(_guard, edited, new HashSet<int> { shapeIdx, childIdx }, expectHeader: false));
    }

    // probe: "gate 2 REFUSES a no-op write (read-back != requested)"
    [Fact]
    public void ReadBackGateRefusesANoOpWrite()
    {
        var pre = NifService.Inspect(_guard).Inspect!;
        var err = NifService.VerifyReadBack(_guard, pre, new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0xABCDE) }, out _);
        Assert.Contains("did NOT take effect", err);
    }

    // probe: "gate 2 PASSES when read-back matches the request"
    [Fact]
    public void ReadBackGatePassesWhenTheValueIsPresent()
    {
        var pre = NifService.Inspect(_guard).Inspect!;
        Assert.Null(NifService.VerifyReadBack(_guard, pre, new[] { new NifSetOp(NifSetOpKind.SetFlags, "GuardShape", Flags: 0x400000E) }, out _));
    }
}
