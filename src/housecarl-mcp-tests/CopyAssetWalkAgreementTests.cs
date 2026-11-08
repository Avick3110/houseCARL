using HousecarlCore;
using HousecarlMcp;
using Mutagen.Bethesda.Plugins;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The few answers copy's walk (<see cref="ClosureWalk.Run"/>) and the asset walk must give alike, though each
/// keeps its own loop (#1315): over one graph both reach whole, the same records in the same breadth-first order, each
/// depth's group read once, and a null link skipped. Both read through the chain the same builder makes.</summary>
[Trait("tier", "unit")]
public sealed class CopyAssetWalkAgreementTests : IClassFixture<AssetWalkPinWorld>, IClassFixture<UnreadLinksFixture>
{
    readonly AssetWalkPinWorld _w;
    readonly UnreadLinksWorld _u;
    public CopyAssetWalkAgreementTests(AssetWalkPinWorld w, UnreadLinksFixture u) => (_w, _u) = (w, u.W);

    const string Seed = "HcPinOrd";

    WalkResult Copy() => _w.Svc.WriteArea.WithSourceChainForGuard(new[] { SourcePoles.Winner }, "from_source", (chain, err) =>
    {
        Assert.Null(err);
        return ClosureWalk.Run(new[] { new WalkSeed(_w.Keys[Seed], "seed", "seed") }, chain!,
                               new WalkScope(fk => fk.ModKey == AssetWalkPinWorld.A), Array.Empty<WalkExclusion>());
    });

    AssetWalkData Asset() => _w.Svc.AssetWalk(Array.Empty<string>(), new[] { _w.Keys[Seed] }, null, null, values: Walks.EveryLink());

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

    /// <summary>OrdB's null extra part is skipped by both: copy succeeds and keeps no null boundary; the asset walk notes
    /// no miss and nothing about the null key.</summary>
    [Fact]
    public void BothSkipANullLink()
    {
        var copy = Copy();
        Assert.True(copy.Success, copy.Refusal?.Detail);
        Assert.DoesNotContain(copy.Kept, k => k.Key.IsNull);
        var asset = Asset();
        Assert.DoesNotContain(asset.Notes, n => n.Contains("none of the sources") || n.Contains(FormIdToken.Of(FormKey.Null)));
    }

    /// <summary>A link set read only leniently is where the two postures part (#1316): the asset walk walks it and
    /// says so; copy, which cannot duplicate the record, refuses at it.</summary>
    [Fact]
    public void ALenientLinkSetIsWalkedWithANoteByOneAndRefusedByTheOther()
    {
        var list = _u.Keys[UnreadLinksWorld.Lenient]["List"];
        var perk = _u.Keys[UnreadLinksWorld.Lenient]["Perk"];
        var copy = _u.Svc.WriteArea.WithSourceChainForGuard(new[] { SourcePoles.Winner }, "from_source", (chain, err) =>
        {
            Assert.Null(err);
            return ClosureWalk.Run(new[] { new WalkSeed(list, "seed", "seed") }, chain!,
                                   new WalkScope(fk => fk.ModKey == list.ModKey), Array.Empty<WalkExclusion>());
        });
        Assert.Equal("UnreadLinks", copy.Refusal?.Kind.ToString());   // named by text, so this case builds against the code before #1316
        Assert.Equal(new[] { list, perk }, copy.Refusal!.Chain);
        var asset = _u.Svc.AssetWalk(Array.Empty<string>(), new[] { list }, null, null, values: Walks.EveryLink());
        Assert.Contains(asset.Nodes, n => n.Path == UnreadLinksWorld.RecoveredNif);
        Assert.Contains(asset.Notes, n => n.Contains(FormIdToken.Of(perk)) && n.Contains("read leniently"));
    }
}
