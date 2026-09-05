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
    public void AbsentParametersAndNullLinksAreOmitted()
    {
        var rows = Conditions(Rows("Conditions"));
        Assert.DoesNotContain("(absent)", rows);
        Assert.DoesNotContain("(null link)", rows);
        // The same read unfolded carries both — the omission is this form's doing, not the record's.
        var fields = Conditions(new RecordsTools.RecordsProject { form = "fields", fields = new[] { "Conditions" }, depth = 4 });
        Assert.Contains("(absent)", fields);
        Assert.Contains("(null link)", fields);
    }

    [Fact]
    public void AnUnreadableSubFieldIsNotAnAbsentOne()
    {
        // A read fault must survive the fold: dropping it would report "I could not look" as "nothing is there".
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
        // A spell effect is an ordinary struct list with an ordinary substruct — same one line per element.
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA) }, project: Rows("Effects"));
        var row = RowLine(r, "Effects[0]");
        Assert.Contains("Data.Magnitude=5", row);
        Assert.DoesNotContain("Effects[0].", r);
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
