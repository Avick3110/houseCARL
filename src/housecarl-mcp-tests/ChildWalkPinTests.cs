using System.Reflection;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The child walk over Mutagen's real record types, pinned (Stryker row T01). <c>ChildBearingProperties</c>,
/// <c>OwnedRecordTypeOf</c> and the slot finder behind <c>TryResolveChildSlot</c> are reflection walks whose answer
/// is a property of Mutagen's model, so the pin is the answer they give today over every concrete record type; a
/// change to the walk that widens or narrows it shows up as a line added or dropped here.
/// </summary>
[Trait("tier", "unit")]
public sealed class ChildWalkPinTests
{
    /// <summary>Every concrete record class Mutagen's Skyrim assembly models.</summary>
    static IReadOnlyList<Type> RecordTypes() =>
        typeof(Weapon).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic
                     && !t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)
                     && typeof(IMajorRecord).IsAssignableFrom(t))
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToList();

    static string Lines(IEnumerable<string> lines) => string.Join("\n", lines.OrderBy(l => l, StringComparer.Ordinal));

    /// <summary>The settable, non-indexed properties of each record type that reach an owned record.</summary>
    [Fact]
    public void ChildBearingPropertiesOverEveryRecordTypeAreTheKnownSet()
    {
        var actual = RecordTypes()
            .SelectMany(t => WriteEngine.ChildBearingProperties(t).Select(p => $"{t.Name}.{p.Name}"));

        Assert.Equal(Lines(ExpectedChildBearing), Lines(actual));
    }

    /// <summary>The record type each property of a record class or its getter interface reaches, read-side walk
    /// (through interfaces too).</summary>
    [Fact]
    public void OwnedRecordTypeOfOverEveryRecordPropertyIsTheKnownSet()
    {
        var actual = new List<string>();
        foreach (var t in RecordTypes())
        {
            var getter = WriteEngine.PrimaryGetter(t);
            foreach (var owner in getter is null ? new[] { t } : new[] { t, getter })
                foreach (var p in owner.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.GetIndexParameters().Length == 0 && WriteEngine.OwnedRecordTypeOf(p.PropertyType) is { } rec)
                        actual.Add($"{owner.Name}.{p.Name}>{rec.Name}");
        }

        Assert.Equal(Lines(ExpectedOwnedRecordType), Lines(actual.Distinct()));
    }

    /// <summary>For every (child, parent) pair of record types, what <c>TryResolveChildSlot</c> answers when it does
    /// not simply refuse: the slot it resolves, or the routes its refusal names (collections, a coordinate route).
    /// Singular and collection slots are both found here, so a read-only or non-record property admitted as a slot
    /// adds a line.</summary>
    [Fact]
    public void TryResolveChildSlotOverEveryRecordPairIsTheKnownSet()
    {
        var actual = new List<string>();
        var types = RecordTypes();
        foreach (var parent in types)
            foreach (var child in types)
            {
                if (WriteEngine.TryResolveChildSlot(child.Name, parent, null, out var slot, out var shape, out var why))
                {
                    actual.Add($"{parent.Name}>{child.Name}: {slot} ({shape})");
                    continue;
                }
                var routes = System.Text.RegularExpressions.Regex.Matches(why ?? "", @"collection=\w+")
                    .Select(m => m.Value).ToList();
                // The coordinate route, not the fixed hint every non-coordinate parent of a Cell gets.
                if ((why ?? "").Contains("by coordinate", StringComparison.Ordinal)) routes.Add("grid");
                if (routes.Count > 0) actual.Add($"{parent.Name}>{child.Name}: refused [{string.Join(", ", routes)}]");
            }

        Assert.Equal(Lines(ExpectedSlots), Lines(actual));
    }

    /// <summary>The row's named pairs, stated on their own so a reader need not search the tables: a Worldspace
    /// files a Cell by coordinate, a Cell holds its placed objects in a named slot and has no coordinate route.</summary>
    [Fact]
    public void AWorldspaceFilesACellByCoordinateAndACellHoldsPlacedObjectsInASlot()
    {
        Assert.False(WriteEngine.TryResolveChildSlot("Cell", typeof(Worldspace), null, out _, out _, out var cellWhy));
        Assert.Contains("grid=", cellWhy);

        Assert.False(WriteEngine.TryResolveChildSlot("PlacedObject", typeof(Cell), null, out _, out _, out var refWhy));
        Assert.DoesNotContain("grid=", refWhy);
        Assert.Contains("collection=", refWhy);
    }

    static readonly string[] ExpectedChildBearing =
    {
        "Cell.Landscape",
        "Cell.NavigationMeshes",
        "Cell.Persistent",
        "Cell.Temporary",
        "DialogTopic.Responses",
        "Worldspace.SubCells",
        "Worldspace.TopCell",
    };
    static readonly string[] ExpectedOwnedRecordType =
    {
        "Cell.Landscape>Landscape",
        "Cell.NavigationMeshes>NavigationMesh",
        "Cell.Persistent>IPlaced",
        "Cell.Temporary>IPlaced",
        "DialogTopic.Responses>DialogResponses",
        "ICellGetter.Landscape>ILandscapeGetter",
        "ICellGetter.NavigationMeshes>INavigationMeshGetter",
        "ICellGetter.Persistent>IPlacedGetter",
        "ICellGetter.Temporary>IPlacedGetter",
        "IDialogTopicGetter.Responses>IDialogResponsesGetter",
        "IWorldspaceGetter.SubCells>ICellGetter",
        "IWorldspaceGetter.TopCell>ICellGetter",
        "Worldspace.SubCells>Cell",
        "Worldspace.TopCell>Cell",
    };
    static readonly string[] ExpectedSlots =
    {
        "Cell>Landscape: Landscape (Singular)",
        "Cell>NavigationMesh: NavigationMeshes (Collection)",
        "Cell>PlacedArrow: refused [collection=Persistent, collection=Temporary]",
        "Cell>PlacedBarrier: refused [collection=Persistent, collection=Temporary]",
        "Cell>PlacedBeam: refused [collection=Persistent, collection=Temporary]",
        "Cell>PlacedCone: refused [collection=Persistent, collection=Temporary]",
        "Cell>PlacedFlame: refused [collection=Persistent, collection=Temporary]",
        "Cell>PlacedHazard: refused [collection=Persistent, collection=Temporary]",
        "Cell>PlacedMissile: refused [collection=Persistent, collection=Temporary]",
        "Cell>PlacedNpc: refused [collection=Persistent, collection=Temporary]",
        "Cell>PlacedObject: refused [collection=Persistent, collection=Temporary]",
        "Cell>PlacedTrap: refused [collection=Persistent, collection=Temporary]",
        "DialogTopic>DialogResponses: Responses (Collection)",
        "Worldspace>Cell: refused [collection=TopCell, grid]",
    };
}
