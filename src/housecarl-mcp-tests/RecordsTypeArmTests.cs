using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A type filter naming one ARM of an abstract group (GlobalShort, GameSettingInt) answers over that arm alone.
/// Mutagen's typed enumeration seeks the GRUP, which for an abstract group is every arm of it, so the scan lane has
/// to re-check the arm on each record — otherwise the answer is the whole group with a corrupted denominator and
/// nothing saying the filter widened.
/// </summary>
[Collection("type-arms")]
[Trait("tier", "integration")]
public sealed class RecordsTypeArmTests
{
    readonly TypeArmWorld _w;
    public RecordsTypeArmTests(TypeArmFixture f) => _w = f.W;

    [Fact]
    public void AnArmTypeFilterOnTheInOrderScanReturnsThatArmAlone()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "GlobalShort" });

        Assert.Contains(TypeArmWorld.Short1, r, StringComparison.Ordinal);
        Assert.Contains(TypeArmWorld.Short2, r, StringComparison.Ordinal);
        Assert.DoesNotContain(TypeArmWorld.Int1, r, StringComparison.Ordinal);
        Assert.DoesNotContain(TypeArmWorld.Float1, r, StringComparison.Ordinal);
    }

    /// <summary>A second arm of the same group, so the filter is known to select rather than to happen to keep the
    /// first arm's records.</summary>
    [Fact]
    public void TheOtherArmOfTheSameGroupSelectsItsOwnRecords()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "GlobalFloat" });

        Assert.Contains(TypeArmWorld.Float1, r, StringComparison.Ordinal);
        Assert.DoesNotContain(TypeArmWorld.Short1, r, StringComparison.Ordinal);
        Assert.DoesNotContain(TypeArmWorld.Int1, r, StringComparison.Ordinal);
    }

    /// <summary>The same shape on a second abstract group, so the fix is not one type's special case.</summary>
    [Fact]
    public void AGameSettingArmFilterAnswersOverThatArmAlone()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "GameSettingInt" });

        Assert.Contains(TypeArmWorld.GmstInt, r, StringComparison.Ordinal);
        Assert.DoesNotContain(TypeArmWorld.GmstFloat, r, StringComparison.Ordinal);
    }

    /// <summary>The plugin-scoped lane runs its own enumeration, so it needs the same arm re-check.</summary>
    [Fact]
    public void AnArmTypeFilterOnThePluginScopedScanReturnsThatArmAlone()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "GlobalShort" },
                                     plugins: new RecordsTools.RecordsScope { names = new[] { _w.MasterName } });

        Assert.Contains(TypeArmWorld.Short1, r, StringComparison.Ordinal);
        Assert.DoesNotContain(TypeArmWorld.Float1, r, StringComparison.Ordinal);
    }

    /// <summary>The whole abstract group is still addressable by its own name — narrowing the arms must not narrow
    /// the group.</summary>
    [Fact]
    public void TheAbstractGroupNameStillAnswersOverEveryArm()
    {
        var r = RecordsTools.Records(_w.Svc, types: new[] { "GLOB" });

        Assert.Contains(TypeArmWorld.Short1, r, StringComparison.Ordinal);
        Assert.Contains(TypeArmWorld.Int1, r, StringComparison.Ordinal);
        Assert.Contains(TypeArmWorld.Float1, r, StringComparison.Ordinal);
    }
}
