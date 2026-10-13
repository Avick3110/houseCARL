using HousecarlCore;
using HousecarlGenerator;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The generated schema and the read walk name the same members, so a where= path the schema refuses is one the walk could not read.</summary>
[Trait("tier", "integration")]
public sealed class SchemaWalkAgreementTests
{
    static readonly Corpus Corpus = CorpusRulebook.LoadCorpus(TestCorpus.Path);

    // Every corpus field resolves through the read walk on the getter, the overlay class and the concrete class.
    [Fact]
    public void EveryCorpusFieldResolvesOnEveryTypeTheWalkMeets()
    {
        var misses = new List<string>();
        foreach (var (ts, _, t) in WalkedTypes())
            foreach (var f in ts.Fields)
                if (WriteEngine.ResolveProperty(t, f.Name) is null) misses.Add($"{ts.Name}.{f.Name} on {t.Name}");
        Assert.True(misses.Count == 0, $"{misses.Count} corpus field(s) the walk cannot resolve:\n" + string.Join("\n", misses.Take(20)));
    }

    // Every member the walk resolves on those types is a corpus field or a listed generator drop.
    [Fact]
    public void EveryMemberTheWalkResolvesIsInTheCorpusOrAListedDrop()
    {
        var extra = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (ts, getter, t) in WalkedTypes())
        {
            var names = ts.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var p in WriteEngine.WalkMembers(t))
                if (!names.Contains(p.Name) && CorpusGenerator.DroppedMember(getter, t, p) is null)
                    extra.Add($"{ts.Name}.{p.Name} on {t.Name}");
        }
        Assert.True(extra.Count == 0, $"{extra.Count} member(s) the walk reads that the corpus lacks:\n" + string.Join("\n", extra.Take(30)));
    }

    // The condition-data parameters the concrete class carries are fields on every arm.
    [Fact]
    public void EveryConditionArmCarriesItsParameters()
    {
        var arms = Corpus.Types.Values.Where(t => t is { Kind: "arm", AbstractBase: "ConditionData" }).ToList();
        Assert.True(arms.Count > 400, $"only {arms.Count} condition arms");
        Assert.All(arms, a => Assert.Contains(a.Fields, f => f.Name == "Parameter1"));
    }

    // An Array2d type's two indexers do not make the walk throw.
    [Fact]
    public void TheWalkDoesNotThrowOnAnArray2dIndexer() =>
        Assert.Null(WriteEngine.ResolveProperty(typeof(Noggog.Array2d<int>), "Item"));

    /// <summary>Each catalog type with the types the walk can meet for it: the getter, the concrete class and its overlay class.</summary>
    static IEnumerable<(TypeSchema Schema, Type Getter, Type Walked)> WalkedTypes()
    {
        foreach (var ts in Corpus.Types.Values)
        {
            if (ts.Kind == "enum" || WriteEngine.ResolveType(ts.GetterInterfaceAssemblyQualified) is not { } getter) continue;
            var concrete = CorpusGenerator.ConcreteClassFor(getter);
            var overlay = concrete is null ? null : CorpusGenerator.OverlayClassFor(concrete);
            // A type reads meet only as its own class has no getter-only view to check.
            if (concrete is null || overlay is not null) yield return (ts, getter, getter);
            if (concrete is not null) yield return (ts, getter, concrete);
            if (overlay is not null) yield return (ts, getter, overlay);
        }
    }
}
