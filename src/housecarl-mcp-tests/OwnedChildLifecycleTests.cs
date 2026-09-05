using HousecarlCore;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The lifecycle of a SINGULAR owned child record — the shape a cell's <c>Landscape</c> and a worldspace's
/// <c>TopCell</c> have (#350). Neither half existed: a parent carrying none could not be given one, because the
/// create lane resolved <c>parent=</c> only into LIST-shaped child slots; and an existing one could not be
/// deleted, because Mutagen's typed <c>Remove(FormKey, Type)</c> routes by group and a singular child is in none.
///
/// <para>Engine-level and world-free: both halves are decided by reflection over Mutagen's own model, so a
/// synthetic mod in memory is the whole world they need. The record types named here are SUBJECTS, not coverage —
/// what is covered is whatever <c>ChildBearingProperties</c> answers.</para>
/// </summary>
[Trait("tier", "unit")]
public sealed class OwnedChildLifecycleTests
{
    static SkyrimMod Mod() => new(new ModKey("HcLifecycle", ModType.Plugin), SkyrimRelease.SkyrimSE);

    /// <summary>A patch holding one interior cell, through the engine's own create path.</summary>
    static (SkyrimMod Mod, Cell Cell) WithCell()
    {
        var mod = Mod();
        return (mod, WriteEngine.AddInteriorCell(mod, "HcLifecycleCell"));
    }

    // ---- half 1: create a singular owned child where the parent has none ----

    /// <summary>The pre-flight gate now ANSWERS for a singular slot. It used to refuse — the resolver filtered to
    /// list-shaped properties before it ever asked about the child type, so a slot holding exactly one record was
    /// invisible to create while every other part of the engine could see it.</summary>
    [Fact]
    public void ASingularOwnedChildCanBeCreatedUnderItsParent()
    {
        Assert.True(WriteEngine.CanCreateNested("Landscape", typeof(Cell), null, out var why), why);
    }

    /// <summary>…and the create actually attaches it: the parent that carried no child carries one afterwards, with
    /// its own allocated FormKey, reachable by Mutagen's containment walk like any other record.</summary>
    [Fact]
    public void CreatingASingularOwnedChildAttachesItToTheParent()
    {
        var (mod, cell) = WithCell();
        Assert.Null(cell.Landscape);

        var child = WriteEngine.NestedAddNew(mod, cell, "Landscape", null, "HcLifecycleLand");

        Assert.NotNull(cell.Landscape);
        Assert.Equal(child.FormKey, cell.Landscape!.FormKey);
        Assert.Equal("HcLifecycleLand", cell.Landscape.EditorID);
        Assert.Contains(mod.EnumerateMajorRecords(), r => r.FormKey == child.FormKey);
    }

    /// <summary>A singular slot holds exactly ONE, so an occupied one is refused rather than resolved: appending is
    /// not available and overwriting would drop the record already there, with everything under it, as a side
    /// effect of a call that said "create". The refusal names both real moves.</summary>
    [Fact]
    public void CreatingASecondSingularChildIsRefusedAndNamesBothMoves()
    {
        var (mod, cell) = WithCell();
        WriteEngine.NestedAddNew(mod, cell, "Landscape", null, "HcLifecycleLand");

        var ex = Assert.Throws<InvalidOperationException>(
            () => WriteEngine.NestedAddNew(mod, cell, "Landscape", null, "HcLifecycleLand2"));

        Assert.Contains("already holds", ex.Message);
        Assert.Contains("housecarl_remove", ex.Message);
    }

    /// <summary>A CELL under a worldspace is the one child that has two routes — the worldspace's single TopCell,
    /// and its coordinate-keyed block tree — and they build different things. One reflected slot matching is not
    /// enough to resolve it, so it is named rather than guessed; resolving it silently would build a top cell for
    /// the far commoner request whose grid= was forgotten.</summary>
    [Fact]
    public void ACellUnderAWorldspaceMustNameWhichRoute()
    {
        Assert.False(WriteEngine.CanCreateNested("Cell", typeof(Worldspace), null, out var why));

        Assert.Contains("TopCell", why!);
        Assert.Contains("grid=", why);
    }

    /// <summary>…and naming the slot resolves it. The discriminator that picks between two collections picks a
    /// singular slot the same way — it names a child SLOT, and the shape only decides how the child is attached.</summary>
    [Fact]
    public void NamingTheSingularSlotResolvesTheAmbiguity()
    {
        Assert.True(WriteEngine.CanCreateNested("Cell", typeof(Worldspace), "TopCell", out var why), why);
    }

    /// <summary>A real containment boundary still refuses: a type the parent models no slot for is not created
    /// under it, and the refusal says that rather than inventing a place to put it.</summary>
    [Fact]
    public void ATypeThatFitsNoSlotIsStillRefused()
    {
        Assert.False(WriteEngine.CanCreateNested("Weapon", typeof(Cell), null, out var why));
        Assert.Contains("cannot be created under", why!);
    }

    // ---- half 2: delete an owned child ----

    /// <summary>The measured fact the delete path exists for: Mutagen's typed remove — the blessed drop, the one
    /// that reaches placed references and INFOs — silently does nothing to a singular owned child, and does not
    /// throw. Pinned as a test so a Mutagen bump that fixes it shows up here rather than leaving dead code.</summary>
    [Fact]
    public void TheTypedRemoveDoesNotReachASingularOwnedChild()
    {
        var (mod, cell) = WithCell();
        var child = WriteEngine.NestedAddNew(mod, cell, "Landscape", null, "HcLifecycleLand");

        ((IMajorRecordEnumerable)mod).Remove(child.FormKey, WriteEngine.RemovalTypeFor(child), throwIfUnknown: true);

        Assert.NotNull(cell.Landscape);
        Assert.Contains(mod.EnumerateMajorRecords(), r => r.FormKey == child.FormKey);
    }

    /// <summary>…and the slot finder plus detach is what does reach it: the child is gone from the parent and from
    /// the mod's containment walk, which is the walk the remove lanes verify absence with.</summary>
    [Fact]
    public void DetachingASingularOwnedChildDropsItFromTheMod()
    {
        var (mod, cell) = WithCell();
        var child = WriteEngine.NestedAddNew(mod, cell, "Landscape", null, "HcLifecycleLand");

        Assert.True(OwnedChildLifecycle.TryFindSlot(mod, child.FormKey, out var slot));
        Assert.Equal(OwnedChildShape.Singular, slot.Shape);
        Assert.Equal("Cell.Landscape", slot.Describe());
        Assert.Null(OwnedChildLifecycle.Detach(slot));

        Assert.Null(cell.Landscape);
        Assert.DoesNotContain(mod.EnumerateMajorRecords(), r => r.FormKey == child.FormKey);
    }

    /// <summary>The COLLECTION half of the same shape goes through the same finder, and reports the list it found
    /// the child in — so one delete path serves both shapes rather than a singular special case beside the
    /// list-shaped one that already worked.</summary>
    [Fact]
    public void TheSameFinderReachesAChildInACollectionSlot()
    {
        var (mod, cell) = WithCell();
        var placed = WriteEngine.NestedAddNew(mod, cell, "PlacedObject", "Persistent", "HcLifecyclePlaced");

        Assert.True(OwnedChildLifecycle.TryFindSlot(mod, placed.FormKey, out var slot));
        Assert.Equal(OwnedChildShape.Collection, slot.Shape);
        Assert.Equal("Cell.Persistent", slot.Describe());
        Assert.Null(OwnedChildLifecycle.Detach(slot));

        Assert.DoesNotContain(mod.EnumerateMajorRecords(), r => r.FormKey == placed.FormKey);
    }

    /// <summary>A record that is nobody's child is found in no slot — the answer that keeps the remove lanes'
    /// survivor check the one that speaks for an ordinary record, rather than this path claiming it.</summary>
    [Fact]
    public void ARecordInNoChildSlotIsNotFound()
    {
        var mod = Mod();
        var weapon = new Weapon(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = "HcLifecycleWeapon" };
        mod.Weapons.Add(weapon);

        Assert.False(OwnedChildLifecycle.TryFindSlot(mod, weapon.FormKey, out _));
    }

    /// <summary>The slot set is DERIVED, not listed — and the walk this asserts through is the one CREATE calls,
    /// not the reflected set beside it. For every child-bearing property Mutagen models, the create resolver must
    /// either accept the property BY NAME for the record it holds (a slot a caller can name), or, when the property
    /// only reaches records through containers, refuse and name the coordinate route instead. Those are the two
    /// answers the lane has; a third would be a shape the lifecycle cannot speak for.</summary>
    [Fact]
    public void EveryChildBearingPropertyIsASlotCreateCanNameOrACoordinateRouteItNames()
    {
        int slots = 0, coordinateRoutes = 0;
        foreach (var t in typeof(Weapon).Assembly.GetTypes())
        {
            if (!t.IsClass || t.IsAbstract || !typeof(IMajorRecord).IsAssignableFrom(t)) continue;
            if (t.Name.EndsWith("BinaryOverlay", StringComparison.Ordinal)) continue;
            foreach (var p in WriteEngine.ChildBearingProperties(t))
            {
                var held = HeldRecordType(p.PropertyType);
                if (held is not null)
                {
                    slots++;
                    Assert.True(WriteEngine.CanCreateNested(held.Name, t, p.Name, out var why),
                        $"{t.Name}.{p.Name} holds a {held.Name} but create cannot name it: {why}");
                    continue;
                }
                // A container route: no record to name the property by, so create must refuse the NAME and, asked
                // for the record the tree eventually holds, say how it is really addressed.
                foreach (var leaf in LeafRecordTypesUnder(p.PropertyType, new HashSet<Type>(), 0))
                {
                    coordinateRoutes++;
                    Assert.False(WriteEngine.CanCreateNested(leaf.Name, t, p.Name, out _),
                        $"{t.Name}.{p.Name} is a container tree, not a slot create can add into by name.");
                    Assert.False(WriteEngine.CanCreateNested(leaf.Name, t, null, out var why));
                    Assert.Contains("grid=", why!);
                }
            }
        }
        Assert.True(slots > 0 && coordinateRoutes > 0,
            "both halves answering nothing is this test's subject, not a reason to pass.");
    }

    /// <summary>The record a property holds DIRECTLY — itself, or as its list's element type — else null (it only
    /// reaches records through containers).</summary>
    static Type? HeldRecordType(Type t)
    {
        if (typeof(IMajorRecordGetter).IsAssignableFrom(t)) return t;
        var elem = t.GetInterfaces().Append(t)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))
            ?.GetGenericArguments()[0];
        return elem is not null && typeof(IMajorRecordGetter).IsAssignableFrom(elem) ? elem : null;
    }

    /// <summary>The concrete record types a container tree eventually holds — the test's own walk, deliberately
    /// independent of the engine's, so the two agreeing is a measurement rather than a tautology.</summary>
    static IEnumerable<Type> LeafRecordTypesUnder(Type t, HashSet<Type> seen, int depth)
    {
        if (depth > 6 || !seen.Add(t)) yield break;
        if (typeof(Mutagen.Bethesda.Plugins.IFormLinkGetter).IsAssignableFrom(t)) yield break;
        if (typeof(IMajorRecordGetter).IsAssignableFrom(t))
        {
            if (t.IsClass && !t.IsAbstract) yield return t;
            yield break;
        }
        var elem = t.GetInterfaces().Append(t)
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            ?.GetGenericArguments()[0];
        if (elem is not null)
        {
            foreach (var leaf in LeafRecordTypesUnder(elem, seen, depth + 1)) yield return leaf;
            yield break;
        }
        if (!t.IsClass || t.Namespace?.StartsWith("Mutagen", StringComparison.Ordinal) != true) yield break;
        foreach (var p in t.GetProperties())
            foreach (var leaf in LeafRecordTypesUnder(p.PropertyType, seen, depth + 1))
                yield return leaf;
    }
}
