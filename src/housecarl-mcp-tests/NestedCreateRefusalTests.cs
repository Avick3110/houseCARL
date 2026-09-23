using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The refusals of a nested create that finds no slot (Stryker row T06): each names the route that works for THAT
/// parent and child, and no route that does not exist.
/// </summary>
[Trait("tier", "unit")]
public sealed class NestedCreateRefusalTests
{
    static string Refusal(string child, Type parent, string? collection = null)
    {
        Assert.False(WriteEngine.TryResolveChildSlot(child, parent, collection, out _, out _, out var why));
        return why!;
    }

    /// <summary>A Cell holds no Keyword by any route, so no coordinate route is offered.</summary>
    [Fact]
    public void AChildWithNoSlotAndNoCoordinateRouteIsNotSentToGrid()
        => Assert.DoesNotContain("grid=", Refusal("Keyword", typeof(Cell)));

    [Fact]
    public void ACellUnderANonWorldspaceParentIsToldHowToCreateAnInteriorCell()
        => Assert.Contains("INTERIOR", Refusal("Cell", typeof(DialogTopic)));

    /// <summary>A worldspace block files its cells by coordinate in sub-blocks and has no slot holding a Cell
    /// directly, so the refusal names that parent with grid=.</summary>
    [Fact]
    public void AChildFiledByCoordinateUnderItsParentIsSentToGrid()
        => Assert.Contains("parent=<WorldspaceBlock> + grid=", Refusal("Cell", typeof(WorldspaceBlock)));

    [Fact]
    public void ANamedCollectionTheParentLacksListsTheSlotsItHas()
    {
        var why = Refusal("PlacedObject", typeof(Cell), "Nope");
        Assert.Contains("Persistent", why);
        Assert.Contains("Temporary", why);
        Assert.DoesNotContain("grid=", why);
    }

    [Fact]
    public void ANamedCollectionACoordinateParentLacksAlsoOffersGrid()
        => Assert.Contains("grid=", Refusal("Cell", typeof(Worldspace), "Nope"));
}
