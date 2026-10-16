using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>A whole-record read takes its field list from the corpus parsed once per corpus path, not once per record.</summary>
[Collection(SerialCollection.Name)]   // repoints CorpusRulebook.CorpusPath, which is process-wide
[Trait("tier", "integration")]
public sealed class WholeRecordCorpusParseTests : IDisposable
{
    readonly RenderCostWorld _w = new();

    public void Dispose() => _w.Dispose();

    [Fact]
    public void ReadingManyRecordsInFullParsesTheCorpusOnce()
    {
        // An untyped scan, so the type lookup does not parse the corpus as well.
        var scan = _w.Svc.CrossQuery((IReadOnlyList<string>?)null, null, null, false, new[] { _w.MasterName }, null, 500);
        Assert.Null(scan.Error);
        var ids = scan.Keys.Select(k => k.ToString()).ToArray();
        Assert.True(ids.Length > RenderCostWorld.Weapons, "the read must cross many records for this to say anything");

        var saved = CorpusRulebook.CorpusPath;
        // A path of its own, so nothing has parsed it yet and no other reader counts against it.
        var fresh = _w.Scratch("corpus.json");
        File.Copy(TestCorpus.Path, fresh);
        CorpusRulebook.CorpusPath = fresh;
        try
        {
            var r = RecordsTools.Records(_w.Svc, formids: ids,
                                         project: new RecordsTools.RecordsProject { form = "everything" });
            Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
            Assert.Contains($"batch: {ids.Length} records", r);
            Assert.Contains("EditorID = HcCostKeyword0", r);
            Assert.Equal(1, CorpusRulebook.ParsesOf(fresh));
        }
        finally
        {
            CorpusRulebook.CorpusPath = saved;
        }
    }
}
