using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The few answers copy's walk (<see cref="ClosureWalk.Run"/>) and the asset walk must give alike, though each
/// keeps its own loop (#1315): over one graph both reach whole, the same records in the same breadth-first order, each
/// depth's group read once, and a null link skipped. Both read through the chain the same builder makes.</summary>
[Trait("tier", "unit")]
public sealed class CopyAssetWalkAgreementTests : IClassFixture<AssetWalkPinWorld>
{
    readonly AssetWalkPinWorld _w;
    public CopyAssetWalkAgreementTests(AssetWalkPinWorld w) => _w = w;

    const string Seed = "HcPinOrd";

    WalkResult Copy() => _w.Svc.WriteArea.WithSourceChainForGuard(new[] { SourcePoles.Winner }, "from_source", (chain, err) =>
    {
        Assert.Null(err);
        return ClosureWalk.Run(new[] { new WalkSeed(_w.Keys[Seed], "seed", "seed") }, chain!,
                               new WalkScope(fk => fk.ModKey == AssetWalkPinWorld.A), Array.Empty<WalkExclusion>());
    });

    AssetWalkData Asset() => _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys[Seed] }, null, null);

    /// <summary>The head parts the asset walk entered, in its order: each one's own mesh is a listed node it pulls.</summary>
    List<string> AssetOrder(AssetWalkData d)
        => d.Nodes.Where(n => n.Edge == "listed")
                  .Select(n => AssetWalkPinWorld.OrdParts.Single(e => n.PulledBy.Contains(FormIdToken.Of(_w.Keys[e])))).ToList();

    /// <summary>The group reads of a walk's second run, its first having paid the one-time reads.</summary>
    Dictionary<(string, string), int> Measured(Action walk)
    {
        walk();
        return GroupReadDelta.Reads(_w.Svc, walk);
    }

    /// <summary>Both walks enter the tree breadth-first in link order: a record linked twice, or back up the tree, is
    /// entered once, where it was first met.</summary>
    [Fact]
    public void BothEnterTheSameRecordsInTheSameOrder()
    {
        var copy = Copy();
        Assert.True(copy.Success, copy.Refusal?.Detail);
        Assert.Equal(new[] { Seed }.Concat(AssetWalkPinWorld.OrdParts), copy.Reached.Select(n => n.EditorId));
        Assert.Equal(AssetWalkPinWorld.OrdParts, AssetOrder(Asset()));
    }

    /// <summary>Both walks read each (plugin, group) once per depth: the list at depth 0, the head parts at depths 1 and 2.</summary>
    [Fact]
    public void BothReadEachDepthOnce()
    {
        var expected = new Dictionary<(string, string), int> { [("HcPinA.esp", "FormLists")] = 1, [("HcPinA.esp", "HeadParts")] = 2 };
        Assert.Equal(expected, Measured(() => Copy()));
        Assert.Equal(expected, Measured(() => Asset()));
    }

    /// <summary>OrdB's null extra part is skipped by both: no refusal, no miss note, and nothing entered for it.</summary>
    [Fact]
    public void BothSkipANullLink()
    {
        var copy = Copy();
        Assert.True(copy.Success, copy.Refusal?.Detail);
        Assert.DoesNotContain(copy.Kept, k => k.Key.IsNull);
        var asset = Asset();
        Assert.DoesNotContain(asset.Notes, n => n.Contains("none of the sources") || n.Contains(FormIdToken.Of(FormKey.Null)));
    }
}
