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
            string ReadAll() => RecordsTools.Records(_w.Svc, formids: ids,
                                                     project: new RecordsTools.RecordsProject { form = "everything" });
            var r = ReadAll();
            Assert.False(r.StartsWith("error:", StringComparison.Ordinal), r);
            Assert.Contains($"batch: {ids.Length} records", r);
            Assert.Contains("EditorID = HcCostKeyword0", r);
            Assert.Equal(1, ModeledFieldIndex.LoadsOf(fresh));
            // With the corpus gone, a read that re-parsed per record would fall back to reflection and list other fields.
            File.Delete(fresh);
            // The rows, without the timing and the spill file's name, which differ call to call.
            static string Untimed(string s) => System.Text.RegularExpressions.Regex.Replace(s.Split("\n\nspilled:")[0], @"\d+ ms", "N ms");
            Assert.Equal(Untimed(r), Untimed(ReadAll()));
            Assert.Equal(1, ModeledFieldIndex.LoadsOf(fresh));
        }
        finally
        {
            CorpusRulebook.CorpusPath = saved;
        }
    }

    [Fact]
    public void AMissingCorpusIsTriedOncePerPath()
    {
        var saved = CorpusRulebook.CorpusPath;
        var missing = _w.Scratch("no-corpus.json");
        CorpusRulebook.CorpusPath = missing;
        try
        {
            for (int i = 0; i < 50; i++) Assert.Null(ModeledFieldIndex.FieldsOf("Weapon"));
            Assert.Equal(1, ModeledFieldIndex.LoadsOf(missing));
        }
        finally
        {
            CorpusRulebook.CorpusPath = saved;
        }
    }
}
