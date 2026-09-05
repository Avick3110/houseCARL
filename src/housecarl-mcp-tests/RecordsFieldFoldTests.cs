using System.Text.Json;
using HousecarlCore;
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
        // The cost claim, on the one fixture that can prove it: the over-budget list truncates the moment it is
        // expanded, so a count that reports the number WITHOUT that note never opened it.
        var count = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, project: Fields("Items[*count]"));
        Served(count, "Items[*count] = " + (ReadEngine.MaxExpandNodes + 1));
        Assert.DoesNotContain("expansion truncated", count);
        var dense = Je(RecordsTools.Records(Svc, types: new[] { "SPEL" }, format: "dense", project: Fields("Effects[*count]")));
        var spellA = dense.GetProperty("rows").EnumerateArray().First(x => x[2].GetString() == "HcRecSpellA");
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

    /// <summary>A bracketed index inside the sub-path costs an expansion level of its own, so a depth counted per
    /// dotted SEGMENT stops the read one level short and the record answers "no element carries it" — a confident
    /// wrong answer about a field that is right there.</summary>
    [Fact]
    public void AnIndexedSubPathIsReadDeepEnoughToReachIt()
    {
        Served(Spell("Effects[*].Conditions[0].Data"), "Effects[1].Conditions[0].Data = ");
        Assert.DoesNotContain("no element of", Spell("Effects[*].Conditions[0].Data"));
    }

    /// <summary>The same need, at a depth the caller named: the default of 4 covers the shallower spelling, so the
    /// claim only bites once the caller asks for less than the token needs.</summary>
    [Fact]
    public void TheTokensOwnDepthWinsOverALowerOneTheCallerNamed()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) },
                    project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects[*].Conditions[0]" }, depth = 2 });
        Served(r, "Effects[1].Conditions[0] = ");
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
    public void AnAbsentListIsTheReadsAnswerNotAMisuseOfTheToken()
    {
        // No Effects field on a WEAP at all: the read's own note carries out under the caller's spelling, and the
        // count is NOT answered with a number — a missing field and an empty list are different answers.
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[1]) }, project: Fields("Effects[*count]"));
        Served(r, "Effects[*count]");
        Assert.DoesNotContain("Effects[*count] = 0", r);
        Assert.Contains("no field", Line(r, "Effects[*count]").ToLowerInvariant());
    }

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

    // ---- what the read said, carried through the fold ------------------------------------------------

    /// <summary>The read's own cut is the case the fold could silently shorten: the elements past
    /// <c>MaxExpandNodes</c> are missing from the rows, so the note that NAMES the cut has to survive the fold.
    /// The fixture's over-budget FormList is the only record that reaches it.</summary>
    [Fact]
    public void ATruncatedExpansionStillSaysSoBesideTheShortRows()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, project: Fields("Items[*]"));
        Served(r, "expansion truncated");
        // And with the fold's own remedy, not the fields form's: lowering the depth renders no rows at all.
        Assert.Contains("the fold runs AFTER the read", r);
    }

    [Fact]
    public void TheDenseDocumentCarriesTheCutToo()
    {
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, types: new[] { "FLST" },
                                          format: "dense", project: Fields("Items[*]"), max_chars: 400_000));
        Assert.Contains("expansion truncated", doc.GetProperty("read_note").GetString()!);
    }

    // ---- an empty list is an answer -----------------------------------------------------------------

    /// <summary>An empty list reads back present with zero elements. The rows form passes its summary line
    /// through, so this spelling of the same row shape does too — a dropped path would be indistinguishable from
    /// one that was never asked for.</summary>
    [Fact]
    public void AnEmptyListRendersItsOwnLineAndNotNothing()
    {
        // Effect 0 carries no conditions; effect 1 does — so the same call proves both halves.
        Served(Spell("Effects[0].Conditions[*]"), "Effects[0].Conditions[*]");
        Served(Spell("Effects[1].Conditions[*]"), "Effects[1].Conditions[0]");
    }

    [Fact]
    public void CountOnAnEmptyListIsZero() =>
        Served(Spell("Effects[0].Conditions[*count]"), "Effects[0].Conditions[*count] = 0");

    // ---- dense: a cell belongs to its own element ---------------------------------------------------

    /// <summary>A sub-path column emits no line for an element whose arm does not carry it, so pairing the
    /// columns by POSITION would sit element 1's value on element 0's row and read as element 0's value.</summary>
    [Fact]
    public void ADenseCellSitsBesideItsOwnElementNeverTheNextOne()
    {
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, types: new[] { "SPEL" },
                                          format: "dense", project: Fields("Effects[*]", "Effects[*].Conditions[0]")));
        var rows = doc.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Contains("Data.Magnitude=5", rows[0][3].GetString()!);      // effect 0 — no conditions
        Assert.Equal(JsonValueKind.Null, rows[0][4].ValueKind);
        Assert.Contains("Data.Magnitude=4", rows[1][3].GetString()!);      // effect 1 — the only one with a condition
        Assert.NotEqual(JsonValueKind.Null, rows[1][4].ValueKind);
    }

    [Fact]
    public void TwoDifferentListsCannotShareOneDenseRow() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "SPEL" }, format: "dense",
                                     project: Fields("Effects[*]", "Keywords[*]")),
                "different lists", "one call per list");

    /// <summary>The ceiling is a ROW property: one record's element rows are unbounded, so a cap consulted only
    /// between records lets a single record overrun it.</summary>
    [Fact]
    public void DenseCapsTheElementRowsAndNotOnlyTheRecords()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, types: new[] { "FLST" },
                                     format: "dense", project: Fields("Items[*]"), max_chars: 4000);
        var doc = Je(r);
        Assert.True(doc.GetProperty("truncated").GetBoolean());
        Assert.True(doc.GetProperty("rows").GetArrayLength() < 100, $"rows={doc.GetProperty("rows").GetArrayLength()}");
        Assert.True(r.Length <= 8000, $"len={r.Length}");
        // `rendered` counts RECORDS, so the row count is its own number rather than a figure a consumer infers.
        Assert.Equal(doc.GetProperty("rows").GetArrayLength(), doc.GetProperty("rows_rendered").GetInt32());
    }

    // ---- what the read is actually asked for --------------------------------------------------------

    /// <summary>Two columns quantifying ONE list are one read of it: ReadEngine.ReadFields does not de-duplicate
    /// its targets and spends a single expansion budget across them, so a repeated path walks the list twice on
    /// that one budget. The unquantified column rides along at the caller's own depth.</summary>
    [Fact]
    public void OneListQuantifiedTwiceIsReadOnceAndSiblingsAtTheCallersDepth()
    {
        var (plan, err) = FieldFolds.Parse(new[] { "Effects[*]", "Effects[*].Data.Magnitude", "EditorID" });
        Assert.Null(err);
        var (paths, depths) = (plan! with { Depth = 4 }).Read();
        Assert.Equal(new[] { "Effects", "EditorID" }, paths);
        Assert.Equal(new[] { 4, 1 }, depths);
    }

    /// <summary>An unquantified column beside a quantified one renders at the caller's own depth, so it is READ at
    /// that depth too — expanding it to the token's depth spends the shared budget on lines the render throws
    /// away, and says the read was cut when the caller's own column is one collapsed line.</summary>
    [Fact]
    public void AnUnquantifiedColumnIsReadAtTheCallersOwnDepth()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, project: Fields("Items", "Effects[*]"));
        Served(r, "Items = ");
        Assert.DoesNotContain("expansion truncated", r);
    }

    // ---- one fold, every lane -----------------------------------------------------------------------

    /// <summary>The json document takes the body lane, not the dense render, so it is its own claim.</summary>
    [Fact]
    public void TheJsonDocumentCarriesTheSameElementRows()
    {
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, format: "json", project: Fields("Effects[*]")));
        var paths = doc.GetProperty("records")[0].GetProperty("fields").EnumerateArray()
                       .Select(f => f.GetProperty("path").GetString()).ToArray();
        Assert.Equal(new[] { "Effects[0]", "Effects[1]" }, paths);
    }

    /// <summary>The one rendered line for a row, by its path.</summary>
    static string RowLine(string response, string path) => Line(response, path + " = ");

    /// <summary>The first rendered line whose own text starts with <paramref name="lead"/>.</summary>
    static string Line(string response, string lead)
    {
        var line = response.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(lead, StringComparison.Ordinal));
        Assert.NotNull(line);
        return line!;
    }
}

/// <summary>The quantified projection's ARTIFACT lane: a to_file spill takes a different route from the inline
/// render, and the claim is that both see the same folded fields.</summary>
[Trait("tier", "integration")]
public sealed class RecordsFieldFoldArtifactTests : ArtifactTestBase, IClassFixture<ArtifactFixture>
{
    public RecordsFieldFoldArtifactTests(ArtifactFixture f) : base(f) { }

    [Fact]
    public void TheArtifactCarriesTheSameElementRows()
    {
        var art = Art("fold-elements.jsonl");
        RecordsTools.Records(Svc, types: new[] { "SPEL" }, to_file: art,
                             project: new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Effects[*]" } });
        // The spilled row for the two-effect spell carries the ELEMENT rows and nothing else — not the list's
        // summary line and not the per-sub-field lines the same depth-4 read would emit unfolded.
        var row = File.ReadAllLines(art).First(l => l.Contains("HcRecSpellA", StringComparison.Ordinal));
        var paths = Je(row).GetProperty("fields").EnumerateArray()
                           .Select(f => f.GetProperty("path").GetString()).ToArray();
        Assert.Equal(new[] { "Effects[0]", "Effects[1]" }, paths);
    }
}
