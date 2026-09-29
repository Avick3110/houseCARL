using Xunit;
using static HousecarlMcpTests.NifSetMeshes;

namespace HousecarlMcpTests;

/// <summary>set_path with no texture_slot (#413): an asset-reference header string (a .tri, material or physics-xml
/// path) swaps in the string table, and the cases this form must not touch refuse by name. Migrated from the
/// nif-set-guard probe's header-string arm.</summary>
[Trait("tier", "unit")]
public sealed class NifSetHeaderStringTests
{
    const string Swapped = @"meshes\actors\character\character assets\guardswapped.tri";
    readonly byte[] _guard = Guard();

    NifSetOutcome SetPath(string target, string path) =>
        NifService.Set(_guard, new[] { new NifSetOp(NifSetOpKind.SetPath, target, Path: path) });

    // probe: "a header string swaps and verifies" / "the new string is in the table and the old one is gone"
    [Fact]
    public void AHeaderStringSwapsAndTheOldOneIsGone()
    {
        var o = SetPath(GuardTriPath, Swapped);
        Assert.Null(o.Error);
        var strings = NifService.Inspect(o.WrittenBytes!).Inspect!.HeaderStrings;
        Assert.Contains(Swapped, strings);
        Assert.DoesNotContain(GuardTriPath, strings);
    }

    // probe: "every OTHER header string is untouched (a swap, not an insert)"
    [Fact]
    public void EveryOtherHeaderStringIsUntouched()
    {
        var before = NifService.Inspect(_guard).Inspect!.HeaderStrings;
        var after = NifService.Inspect(SetPath(GuardTriPath, Swapped).WrittenBytes!).Inspect!.HeaderStrings;
        Assert.Equal(before.Count, after.Count);
        Assert.All(before.Where(x => x != GuardTriPath), s => Assert.Contains(s, after));
    }

    // probe: "a LENGTH-CHANGING header-string swap does not false-abort gate 1"
    [Fact]
    public void ALongerHeaderStringSwapDoesNotFalseAbort()
    {
        Assert.Null(SetPath(GuardTriPath, Swapped + "_and_then_some_more").Error);
    }

    // probe: "a shape NAME is refused and sent to rename_shape"
    [Fact]
    public void AShapeNameIsSentToRenameShape()
    {
        var o = SetPath("GuardShape", "Renamed");
        Assert.Contains("rename_shape", o.Error);
        Assert.Null(o.WrittenBytes);
    }

    // probe: "a string no block carries is refused by name"
    [Fact]
    public void AStringNoBlockCarriesIsRefused()
    {
        var o = SetPath(@"meshes\nothing\here.tri", Swapped);
        Assert.Contains("no header string", o.Error);
        Assert.Null(o.WrittenBytes);
    }

    // probe: "matching is case-SENSITIVE — a wrong-case target refuses"
    [Fact]
    public void AWrongCaseTargetRefuses()
    {
        var o = SetPath(GuardTriPath.ToUpperInvariant(), Swapped);
        Assert.NotNull(o.Error);
        Assert.Null(o.WrittenBytes);
    }

    // probe: "swapping a string for itself is refused rather than written as a no-op"
    [Fact]
    public void SwappingAStringForItselfRefuses()
    {
        var o = SetPath(GuardTriPath, GuardTriPath);
        Assert.Contains("already reads", o.Error);
        Assert.Null(o.WrittenBytes);
    }

    // probe: "an extra-data KEY is refused, pointing at the block's VALUE"
    [Fact]
    public void AnExtraDataKeyIsRefused()
    {
        var o = SetPath("BODYTRI", "NOTBODYTRI");
        Assert.Contains("KEY", o.Error);
        Assert.Null(o.WrittenBytes);
    }

    // probe: "a replacement already in the table is refused by name"
    [Fact]
    public void AReplacementAlreadyInTheTableIsRefused()
    {
        var o = SetPath(GuardTriPath, "BODYTRI");
        Assert.Contains("renumber", o.Error);
        Assert.Null(o.WrittenBytes);
    }
}
