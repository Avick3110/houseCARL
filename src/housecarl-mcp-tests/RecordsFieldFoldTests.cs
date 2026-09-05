using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// The PROJECT half of the quantified step: <c>project.fields</c>' <c>[*count]</c> (one number per record) and
/// <c>[*]</c> (one row per element), driven end to end through the tool over the fixture's two-effect spell.
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsFieldFoldTests : RecordsTestBase
{
    public RecordsFieldFoldTests(RecordsFixture f) : base(f) { }

    string Spell(params string[] paths) =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, project: Fields(paths));

    [Fact]
    public void CountOnAListIsOneNumberPerRecord()
    {
        var r = Spell("Effects[*count]");
        Served(r, "Effects[*count] = 2");
        // The number, not the list: no element line survives.
        Assert.DoesNotContain("Effects[0]", r);
    }

    [Fact]
    public void CountReadsTheListWithoutOpeningIt()
    {
        // A count needs no expansion, so the call costs the depth-1 read even though [*] would force depth 4.
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, format: "dense", project: Fields("Effects[*count]"));
        var doc = Je(r);
        Assert.Equal(new[] { "formid", "runtime_formid", "editorid", "Effects[*count]" },
                     doc.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray());
        var spellA = doc.GetProperty("rows").EnumerateArray().First(x => x[2].GetString() == "HcRecSpellA");
        Assert.Equal("2", spellA[3].GetString());
    }

    [Fact]
    public void TheBareStarIsOneRowPerElement_TheRowsFormsOwnRowShape()
    {
        var r = Spell("Effects[*]");
        Served(r, "Effects[0] = ", "Effects[1] = ");
        // Each row carries the element's sub-fields inline, exactly as form='rows' renders them.
        Assert.Contains("Data.Magnitude=5", RowLine(r, "Effects[0]"));
        Assert.Contains("Data.Magnitude=4", RowLine(r, "Effects[1]"));
        // And never as lines of their own — that is the whole difference from naming the list.
        Assert.DoesNotContain("Effects[0].", r);
    }

    [Fact]
    public void AQuantifiedPathComposesWithAnOrdinaryOne()
    {
        var r = Spell("EditorID", "Effects[*]");
        Served(r, "EditorID = HcRecSpellA", "Effects[0] = ", "Effects[1] = ");
    }

    [Fact]
    public void ASubPathAfterTheTokenIsThatLeafPerElement()
    {
        var r = Spell("Effects[*].Data.Magnitude");
        Served(r, "Effects[0].Data.Magnitude = 5", "Effects[1].Data.Magnitude = 4");
    }

    [Fact]
    public void DenseCarriesTheExtraRowsWithTheIdentityColumnsRepeated()
    {
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, types: new[] { "SPEL" },
                                          format: "dense", project: Fields("EditorID", "Effects[*]")));
        Assert.Equal(new[] { "formid", "runtime_formid", "editorid", "EditorID", "Effects[*]" },
                     doc.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToArray());
        var rows = doc.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);                                    // one row per effect
        Assert.Equal(rows[0][0].GetString(), rows[1][0].GetString());   // identity repeated, not omitted
        Assert.Equal("HcRecSpellA", rows[0][3].GetString());            // the unquantified column repeats too
        Assert.Equal("HcRecSpellA", rows[1][3].GetString());
        Assert.Contains("Data.Magnitude=5", rows[0][4].GetString()!);
        Assert.Contains("Data.Magnitude=4", rows[1][4].GetString()!);
    }

    [Fact]
    public void AQuantifierOnANonListPathIsRefusedByName()
    {
        var r = Spell("EditorID[*]");
        Assert.Contains("'EditorID[*]' quantifies a LIST", r);
        Assert.Contains("'EditorID' on Spell", r);
        Assert.Contains("drop the quantifier", r);
    }

    [Fact]
    public void CountOnANonListPathIsRefusedTheSameWay() =>
        Assert.Contains("'EditorID[*count]' quantifies a LIST", Spell("EditorID[*count]"));

    [Fact]
    public void AnAbsentListIsTheReadsAnswerNotAMisuseOfTheToken() =>
        // No Effects field on a WEAP at all: the read says so under the caller's own spelling.
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[1]) }, project: Fields("Effects[*count]")),
               "Effects[*count]");

    [Fact]
    public void ABooleanFoldStaysInWhere() =>
        Refused(Spell("Effects[*any].Data.Magnitude"), "not a row", "where=");

    [Fact]
    public void ANonQuantifierBracketKeyIsNamedATypo() =>
        Refused(Spell("Effects[*sum]"), "is not a quantifier");

    [Fact]
    public void NothingCanFollowCount() =>
        Refused(Spell("Effects[*count].Data"), "nothing can follow '[*count]'");

    [Fact]
    public void TwoQuantifiedStepsInOnePathAreRefused() =>
        Refused(Spell("Effects[*].Conditions[*]"), "quantifies two steps");

    [Fact]
    public void TheTokenBelongsToTheFieldsForm() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) },
                    project: new RecordsTools.RecordsProject { form = "rows", fields = new[] { "Effects[*]" } }),
                "the 'rows' form already folds");

    [Fact]
    public void DepthOneWouldCollapseTheElementsAway() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) },
                    project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects[*]" }, depth = 1 }),
                "Effects[*count]");

    /// <summary>The one rendered line for a row, by its path.</summary>
    static string RowLine(string response, string path)
    {
        var line = response.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(path + " = ", StringComparison.Ordinal));
        Assert.NotNull(line);
        return line!;
    }
}
