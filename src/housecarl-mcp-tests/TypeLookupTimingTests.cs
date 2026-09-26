using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>The type lookup reads the corpus only on a resolution that names a type: a call with no types never
/// builds it, and a build that fails is not kept.</summary>
[Collection(SerialCollection.Name)]   // repoints CorpusRulebook.CorpusPath, which is process-wide
[Trait("tier", "integration")]
public sealed class TypeLookupTimingTests : IDisposable
{
    readonly RenderCostWorld _w = new();

    public void Dispose() => _w.Dispose();

    [Fact]
    public void ACallWithNoTypesNeverReadsTheCorpusAndATypedOneNamesItsError()
    {
        var saved = CorpusRulebook.CorpusPath;
        var missing = Path.Combine(Path.GetTempPath(), "hc-no-corpus-" + Guid.NewGuid().ToString("N"), "corpus.json");
        CorpusRulebook.CorpusPath = missing;
        try
        {
            var svc = _w.Svc;

            // An untyped scan, scoped by plugin only.
            var scan = svc.CrossQuery((IReadOnlyList<string>?)null, null, null, false, new[] { _w.MasterName }, null, 500);
            Assert.Null(scan.Error);
            Assert.True(scan.Total > 0);

            // An off-order scan with no types.
            var pole = svc.ProbeSourceArm(_w.OffOrderName, null, out var perr);
            Assert.Null(perr);
            var off = svc.OffOrderQuery(pole!, null, null, null, null, false, null, 100, null, 0, null, null);
            Assert.Null(off.Error);
            Assert.True(off.Total > 0);

            // The display names of an absent set, both overloads.
            Assert.Null(svc.Types.DisplayNames((IReadOnlyList<string>?)null));
            Assert.Null(TypeLookup.DisplayNames((IReadOnlyList<Type>?)null));

            // The records tool's list-lane aggregate with no types (the display-name call on that path).
            var agg = RecordsTools.Records(svc, formids: new[] { scan.Keys[0].ToString() },
                                           project: new RecordsTools.RecordsProject { form = "aggregate", group_by = "type" });
            Assert.DoesNotContain("corpus.json", agg);
            Assert.DoesNotContain("error", agg, StringComparison.OrdinalIgnoreCase);

            // A typed resolution does read the corpus, and says which file is missing.
            var ex = Assert.Throws<FileNotFoundException>(
                () => svc.ReadArea.CrossQuery("WEAP", null, null, false, null, null, 10));
            Assert.Contains("corpus.json not found at " + Path.GetFullPath(missing), ex.Message);

            // The failed build is not kept: with the corpus back, the same service resolves the type.
            CorpusRulebook.CorpusPath = saved;
            var typed = svc.ReadArea.CrossQuery("WEAP", null, null, false, null, null, 500);
            Assert.Null(typed.Error);
            Assert.Equal(RenderCostWorld.Weapons, typed.Total);
        }
        finally
        {
            CorpusRulebook.CorpusPath = saved;
        }
    }
}
