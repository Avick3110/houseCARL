using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// A where= path that names no field says so on every form, at 0 matches and at some (#1160). An empty FormList
/// decided the '[*any]' fold without reading a leaf, and the body-lane forms dropped the note entirely.
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class WrongPathNoteEveryFormTests : RecordsTestBase
{
    public WrongPathNoteEveryFormTests(RecordsFixture f) : base(f) { }

    static readonly string[] Flst = { "FLST" };

    // The issue's call: 'type' is not a field of a FormList item's target.
    static readonly string[] IssueWhere = { "Items[*any]->type = Armor" };

    string Call(string shape, string[] where) => shape switch
    {
        "summary" => RecordsTools.Records(Svc, types: Flst, where: where),
        "fields" => RecordsTools.Records(Svc, types: Flst, where: where, project: Fields("EditorID")),
        "fields-quantified" => RecordsTools.Records(Svc, types: Flst, where: where, project: Fields("EditorID", "Items[*count]")),
        "everything" => RecordsTools.Records(Svc, types: Flst, where: where, project: Form("everything")),
        "dense" => RecordsTools.Records(Svc, types: Flst, where: where, project: Fields("EditorID"), format: "dense"),
        "json" => RecordsTools.Records(Svc, types: Flst, where: where, project: Fields("EditorID"), format: "json"),
        "everything-json" => RecordsTools.Records(Svc, types: Flst, where: where, project: Form("everything"), format: "json"),
        "counts_only" => RecordsTools.Records(Svc, types: Flst, where: where, project: Fields("EditorID"), counts_only: true),
        "aggregate" => RecordsTools.Records(Svc, types: Flst, where: where, project: new() { form = "aggregate", group_by = "winner" }),
        _ => throw new ArgumentException(shape),
    };

    [Theory]
    [InlineData("summary")]
    [InlineData("fields")]
    [InlineData("fields-quantified")]
    [InlineData("everything")]
    [InlineData("dense")]
    [InlineData("json")]
    [InlineData("everything-json")]
    [InlineData("counts_only")]
    [InlineData("aggregate")]
    public void TheIssuesHopSaysNotAFieldAtZeroMatchesOnEveryForm(string shape)
        => Served(Call(shape, IssueWhere), "NOT A FIELD");

    // The scan lane (fields) and the body lane (everything, a quantified fields path) each write their own manifest.
    [Theory]
    [InlineData("fields")]
    [InlineData("fields-quantified")]
    [InlineData("everything")]
    public void TheIssuesHopSaysNotAFieldInTheToFileManifest(string shape)
    {
        var file = Path.Combine(W.Root, "wrong-path-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var project = shape switch
        {
            "fields" => Fields("EditorID"),
            "fields-quantified" => Fields("EditorID", "Items[*count]"),
            _ => Form("everything"),
        };
        Served(RecordsTools.Records(Svc, types: Flst, where: IssueWhere, project: project, to_file: file), "NOT A FIELD");
        Assert.Contains("NOT A FIELD", File.ReadLines(file).First());
    }

    /// <summary>'[*none]' matches the empty list, so the wrong path comes back with a match and still says so.</summary>
    [Theory]
    [InlineData("summary")]
    [InlineData("fields")]
    [InlineData("everything")]
    public void AWrongHopWithMatchesStillSaysNotAField(string shape)
    {
        var r = Call(shape, new[] { "Items[*none]->type = Armor" });
        Served(r, "NOT A FIELD");
        Assert.Contains(Fid(W.ListDropped), r);   // the winner's copy of this list is empty
    }

    [Theory]
    [InlineData("everything")]
    [InlineData("fields-quantified")]
    public void ATopLevelWrongPathSaysNotAFieldOnTheBodyLane(string shape)
        => Served(Call(shape, new[] { "NoSuchField = 5" }), "NOT A FIELD");

    [Fact]
    public void AParentStepWithNoContainerSaysSoOnTheBodyLane()
        => Served(Call("everything", new[] { "*parent.NoSuch = 5" }), "no record CONTAINS");

    [Theory]
    [InlineData("summary")]
    [InlineData("everything")]
    public void ARealHopPathMatchesWithNoNote(string shape)
    {
        var r = Call(shape, new[] { "Items[*any]->editorid startswith HcRec" });
        Served(r, Fid(W.ListHop));
        Assert.DoesNotContain("NOT A FIELD", r);
        Assert.DoesNotContain("had no value", r);
    }
}
