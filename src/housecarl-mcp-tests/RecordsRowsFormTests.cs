using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// project.form='rows' — a list field folded to one line per element. The condition stack on OtherMgef is the
/// fixture the form was built for; the spell effect beside it is the proof that the fold is general.
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsRowsFormTests : RecordsTestBase
{
    public RecordsRowsFormTests(RecordsFixture f) : base(f) { }

    static RecordsTools.RecordsProject Rows(params string[] paths) =>
        new() { form = "rows", fields = paths };

    string Conditions(RecordsTools.RecordsProject p) =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefB) }, project: p);

    string Effects(RecordsTools.RecordsProject p) =>
        RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, project: p);

    /// <summary>A no-value leaf, for the unit-level folds.</summary>
    static HousecarlCore.FieldValue Fv(string path, string note) =>
        new(path, false, null, note, Present: true);

    /// <summary>Every field path the json document carries for the one record.</summary>
    static List<string?> JsonPaths(string json) =>
        Je(json).GetProperty("records")[0].GetProperty("fields")
                .EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToList();

    [Fact]
    public void AConditionStackIsOneLinePerRow()
    {
        var r = Conditions(Rows("Conditions"));
        Served(r, "Conditions[0] = ", "Conditions[1] = ", "Conditions[2] = ");
        // The whole point: no per-sub-field line survives, so 3 rows cost 3 lines and not 72.
        Assert.DoesNotContain("Conditions[0].", r);
        Assert.DoesNotContain("Conditions[1].", r);
    }

    [Fact]
    public void ARowNamesItsFunctionOperatorValueAndOrFlag()
    {
        var row = RowLine(Conditions(Rows("Conditions")), "Conditions[1]");
        Assert.Contains("Data.Function=GetActorValue", row);
        Assert.Contains("Data.ActorValue=Destruction", row);
        Assert.Contains("CompareOperator=GreaterThanOrEqualTo", row);
        Assert.Contains("ComparisonValue=30", row);
        Assert.Contains("Flags=OR", row);
    }

    [Fact]
    public void AnAbsentParameterIsOmittedAndADeclaredNullLinkIsKept()
    {
        var rows = Conditions(Rows("Conditions"));
        Assert.DoesNotContain("(absent)", rows);
        // A null link is an EMPTY SLOT, not an absence — it is the signal the None-property diagnostics read,
        // so the fold keeps it and only the absent optional goes.
        Assert.Contains("(null link)", rows);
        // The same read unfolded carries both — the omission is this form's doing, not the record's.
        var fields = Conditions(new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Conditions" }, depth = 4 });
        Assert.Contains("(absent)", fields);
        Assert.Contains("(null link)", fields);
    }

    [Fact]
    public void AnUnreadableSubFieldIsNotAnAbsentOne()
    {
        // A read fault must survive the fold: dropping it would report "I could not look" as "nothing is there".
        // Driven at the fold directly on purpose: ReadEngine.Expand skips a nested property-get fault rather than
        // emitting an unreadable line, so no read on this lane can produce one — this pins the fold, not the lane.
        var folded = RowProjection.Fold(new[]
        {
            new HousecarlCore.FieldValue("Conditions[0]", false, null, "[ConditionFloat]", Present: true),
            new HousecarlCore.FieldValue("Conditions[0].Gone", false, null, "(absent)", Present: false),
            new HousecarlCore.FieldValue("Conditions[0].Broken", false, null, "(unreadable: boom)", Present: false, Readable: false),
        }, new[] { "Conditions" });
        Assert.Equal("[ConditionFloat] | Broken=(unreadable: boom)", Assert.Single(folded).Note);
    }

    [Fact]
    public void TheListHeaderKeepsItsTrueCount() =>
        Served(Conditions(Rows("Conditions")), "Conditions = [list: 3 item(s)]");

    [Fact]
    public void TheFoldIsGeneralNotConditionSpecific()
    {
        // A spell effect is an ordinary struct list with an ordinary substruct — same one line per element,
        // over a MULTI-element list, so the row boundary is exercised off the condition lane too.
        var r = Effects(Rows("Effects"));
        Assert.Contains("Data.Magnitude=5", RowLine(r, "Effects[0]"));
        Assert.Contains("Data.Magnitude=11", RowLine(r, "Effects[1]"));
        Assert.DoesNotContain("Effects[0].", r);
        Assert.DoesNotContain("Effects[1].", r);
    }

    [Fact]
    public void ADeclaredNullLinkOnANonConditionElementIsKeptToo() =>
        Assert.Contains("BaseEffect=(null link)", RowLine(Effects(Rows("Effects")), "Effects[1]"));

    [Fact]
    public void ANestedListInsideAnElementFoldsIntoThatElementsRow()
    {
        var r = Effects(Rows("Effects"));
        var row = RowLine(r, "Effects[1]");
        Assert.Contains("Conditions=[list: 1 item(s)]", row);   // the nested list keeps its count
        Assert.Contains("Conditions[0]=[ConditionFloat]", row);
        Assert.DoesNotContain("\n  Effects[1].Conditions", r);  // and never as lines of its own
    }

    [Fact]
    public void AnIndexedRootFoldsExactlyThatElement()
    {
        var r = Conditions(Rows("Conditions[1]"));
        Assert.Contains("Data.Function=GetActorValue", RowLine(r, "Conditions[1]"));
        Assert.DoesNotContain("Conditions[0]", r);
        Assert.DoesNotContain("Conditions[1].", r);
    }

    [Fact]
    public void AFieldThatIsNotAListFailsLoudInsteadOfAnsweringWithTheFieldsForm()
    {
        // Silently answering with the fields form is the failure this refuses: the caller asked for rows.
        // Per RECORD, because only the read knows what the path resolved to on this record's type.
        var r = Conditions(Rows("EditorID"));
        Assert.Contains("error: project.form='rows' folds a LIST field", r);
        Assert.Contains("'EditorID' on MagicEffect", r);
        Assert.Contains("project.form='fields'", r);
    }

    [Fact]
    public void AnAbsentOrUnknownFieldIsTheReadsAnswerNotAMisuseOfTheForm() =>
        // No such field on a WEAP: that is what the read found, and the fields form's own note says it.
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[1]) }, project: Rows("Conditions")),
               "Conditions");

    [Fact]
    public void OverlappingRootsGiveOneEntryPerElement()
    {
        // The longer root claims the nested lines, so the outer element's own lines can resume after them; a row
        // is keyed by its PATH, so what resumes rejoins the row it belongs to instead of opening a second one.
        var folded = RowProjection.Fold(new[]
        {
            Fv("Effects[0]", "[Effect]"),
            Fv("Effects[0].BaseEffect", "(null link)"),
            Fv("Effects[0].Conditions[0]", "[ConditionFloat]"),
            Fv("Effects[0].Flags", "0"),
        }, new[] { "Effects[0].Conditions", "Effects" }.OrderByDescending(s => s.Length).ToList());
        Assert.Equal(new[] { "Effects[0]", "Effects[0].Conditions[0]" }, folded.Select(f => f.Path));
        Assert.Contains("Flags=0", folded[0].Note);
    }

    [Fact]
    public void TheJsonDocumentNeverCarriesTwoEntriesForOnePath()
    {
        var paths = JsonPaths(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, format: "json",
                                                   project: Rows("Effects", "Effects[1].Conditions")));
        Assert.Equal(paths.Distinct().Count(), paths.Count);
    }

    [Fact]
    public void ARowsJsonEntryCarriesItsCellsStructurallyNotAsANote()
    {
        var row = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, format: "json",
                         project: new RecordsTools.RecordsProject { form = "rows", fields = new[] { "Effects" }, resolve_names = true }))
                  .GetProperty("records")[0].GetProperty("fields")[1];
        Assert.Equal("Effects[0]", row.GetProperty("path").GetString());
        // The row's DATA is cells, never prose in a note: a value stays a value and resolve_names stays a link
        // object, exactly as they are on the fields form.
        Assert.False(row.TryGetProperty("note", out _));
        var cells = row.GetProperty("cells").EnumerateArray().ToList();
        Assert.Equal("5", cells.Single(c => c.GetProperty("path").GetString() == "Effects[0].Data.Magnitude").GetProperty("value").GetString());
        Assert.Equal("HcRecMgefFire", cells.Single(c => c.GetProperty("path").GetString() == "Effects[0].BaseEffect")
                                           .GetProperty("link").GetProperty("editorid").GetString());
    }

    [Fact]
    public void TheTruncationNoteOffersARemedyThisFormCanTake()
    {
        // The engine's own remedy ("a lower depth") points at depths this form refuses or renders bare, so the
        // fold restates it. The cut itself is the engine's and is unchanged.
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.BigList) }, project: Rows("Items"));
        Assert.Contains("expansion truncated at", r);
        Assert.Contains("the fold runs AFTER the read", r);
        Assert.Contains("lower depth to 3", r);
    }

    [Fact]
    public void TheOffOrderArmFoldsToo()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, source: Plugin(W.OldName),
                                     project: Rows("Effects"));
        Assert.Contains("Data.Magnitude=11", RowLine(r, "Effects[1]"));
        Assert.DoesNotContain("Effects[1].", r);
    }

    [Fact]
    public void AWalkedReachedSetFolds() =>
        Served(RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, walk: new RecordsTools.RecordsWalk(),
                                    project: Rows("Effects")), "Effects[1] = ");

    [Fact]
    public void ToFileSpillsTheFoldedRows()
    {
        var art = W.Scratch("results", "rows.jsonl");
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, project: Rows("Effects"), to_file: art);
        Assert.Contains(art, r);
        var body = File.ReadAllText(art);
        Assert.Contains("Effects[1]", body);
        Assert.Contains("cells", body);
    }

    [Fact]
    public void ResolveNamesAnnotatesInsideARow()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) },
            project: new RecordsTools.RecordsProject { form = "rows", fields = new[] { "Effects" }, resolve_names = true });
        Assert.Contains("HcRecMgefFire", RowLine(r, "Effects[0]"));
    }

    [Fact]
    public void ADepthOfTwoLeavesTheRowAtItsElementType()
    {
        // depth= means the same thing here as on the fields form: how far into each element the line reaches.
        var row = RowLine(Conditions(new RecordsTools.RecordsProject { form = "rows", fields = new[] { "Conditions" }, depth = 2 }), "Conditions[0]");
        Assert.Contains("[ConditionFloat]", row);
        Assert.DoesNotContain("Data.ActorValue", row);
    }

    [Fact]
    public void ADepthOfOneWouldRenderNoRowsAndIsRefused() =>
        Refused(Conditions(new RecordsTools.RecordsProject { form = "rows", fields = new[] { "Conditions" }, depth = 1 }),
                "project.depth=1", "depth >= 2");

    [Fact]
    public void AScanFoldsEveryMatch()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "MGEF" }, project: Rows("Conditions"));
        Served(r, "Conditions[2] = ", "Conditions = [list: 0 item(s)]");
        Assert.DoesNotContain("Conditions[2].", r);
    }

    [Fact]
    public void TheJsonDocumentCarriesOneEntryPerElement()
    {
        var doc = Je(RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefB) }, format: "json", project: Rows("Conditions")));
        var paths = doc.GetProperty("records")[0].GetProperty("fields")
                       .EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToList();
        Assert.Equal(new[] { "Conditions", "Conditions[0]", "Conditions[1]", "Conditions[2]" }, paths);
    }

    [Fact]
    public void TheFormNamesItsListField() =>
        Refused(RecordsTools.Records(Svc, formids: new[] { Fid(W.MgefB) }, project: Form("rows")),
                "project.fields", "Conditions");

    [Fact]
    public void DenseRefusesTheFormByName() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "MGEF" }, format: "dense", project: Rows("Conditions")),
                "dense", "rows");

    [Fact]
    public void GroupByStaysOnTheAggregateForm() =>
        Refused(RecordsTools.Records(Svc, types: new[] { "MGEF" },
                    project: new RecordsTools.RecordsProject { form = "rows", fields = new[] { "Conditions" }, group_by = "winner" }),
                "project.group_by", "aggregate");

    /// <summary>The one rendered line for a row, by its path — the unit every assertion here is about.</summary>
    static string RowLine(string response, string path)
    {
        Assert.False(response.StartsWith("error:", StringComparison.Ordinal), "refused: " + First(response));
        var line = response.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(path + " = ", StringComparison.Ordinal));
        Assert.NotNull(line);
        return line!;
    }
}
