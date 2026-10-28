using System.Text.Json;
using HousecarlCore;
using HousecarlMcp;
using Xunit;

namespace HousecarlMcpTests;

/// <summary>
/// project.fields is planned once per call through the schema walk where= uses (#1093): a path no type in scope
/// carries refuses the call, and a path one type lacks reads as '(not on Type)' on that type's rows with the
/// diagnosis said once, so a mixed-type answer is not the same ~130-char note repeated per row.
/// </summary>
[Collection("records")]
[Trait("tier", "integration")]
public sealed class RecordsProjectionPlanTests : RecordsTestBase
{
    public RecordsProjectionPlanTests(RecordsFixture f) : base(f) { }

    static readonly string[] Mixed = { "WEAP", "ARMO" };
    const string Token = "(not on Weapon)";
    const string NoteHead = "note: project.fields 'ArmorRating'";

    string[] WeaponAndArmor => new[] { Fid(W.Weapons[0]), Fid(W.Armor) };

    static JsonElement Doc(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static string FieldLine(string r, string path) =>
        r.Split('\n').First(l => l.TrimStart().StartsWith(path + " = ", StringComparison.Ordinal));

    [Fact]
    public void APlannedLackingPathReadsTheTokenNotTheDiagnosis()
    {
        var (plan, refusal) = PlannedFields.Of(TestCorpus.Rulebook.PathWalk, new[] { "ArmorRating" }, null);
        Assert.Null(refusal);
        var body = W.WeaponBodies.First(b => b.FormKey == W.Weapons[0]);
        var f = ReadEngine.ReadFields(body, plan).Fields.Single();
        Assert.Equal(Token, f.Note);
        Assert.False(ReadEngine.IsNoSuchFieldNote(f.Note));
        Assert.Single(plan!.Notes());
    }

    [Fact]
    public void AMixedTypeScanSaysTheNoteOnceAndEachRowTheToken()
    {
        var r = RecordsTools.Records(Svc, types: Mixed, project: Fields("ArmorRating"));
        Served(r);
        Assert.Equal(1, CountOf(r, NoteHead));
        Assert.True(CountOf(r, "type=Weapon") > 1, "the scan must render more than one lacking row for this to say anything");
        Assert.Equal(CountOf(r, "type=Weapon"), CountOf(r, "ArmorRating = " + Token));
        Assert.DoesNotContain("(no field", r);
    }

    [Fact]
    public void AMixedTypeListSaysTheNoteOnceInTheHeader()
    {
        var r = RecordsTools.Records(Svc, formids: WeaponAndArmor, project: Fields("ArmorRating"));
        Served(r);
        Assert.Equal(1, CountOf(r, NoteHead));
        Assert.True(r.IndexOf(NoteHead, StringComparison.Ordinal) < r.IndexOf("batch:", StringComparison.Ordinal), "the note rides the header");
        Assert.Equal(Token, FieldLine(r, "ArmorRating").Split(" = ")[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonCarriesTheNoteOnceAndTheTokenPerLeaf(bool scan)
    {
        var r = scan ? RecordsTools.Records(Svc, types: Mixed, format: "json", project: Fields("ArmorRating"))
                     : RecordsTools.Records(Svc, formids: WeaponAndArmor, format: "json", project: Fields("ArmorRating"));
        var doc = Doc(r);
        Assert.Single(doc.GetProperty("notes").EnumerateArray(), n => n.GetString()!.StartsWith(NoteHead, StringComparison.Ordinal));
        var rows = doc.GetProperty(scan ? "matches" : "records").EnumerateArray().ToList();
        var weapons = rows.Where(x => x.GetProperty("type").GetString() == "Weapon").ToList();
        Assert.NotEmpty(weapons);
        Assert.All(weapons, w => Assert.Equal(Token, w.GetProperty("fields")[0].GetProperty("note").GetString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DenseKeepsOneCellPerColumnAndTheCellIsTheToken(bool scan)
    {
        var r = scan ? RecordsTools.Records(Svc, types: Mixed, format: "dense", project: Fields("ArmorRating", "EditorID"))
                     : RecordsTools.Records(Svc, formids: WeaponAndArmor, format: "dense", project: Fields("ArmorRating", "EditorID"));
        var doc = Doc(r);
        var cols = doc.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
        int at = cols.IndexOf("ArmorRating");
        Assert.Single(doc.GetProperty("notes").EnumerateArray(), n => n.GetString()!.StartsWith(NoteHead, StringComparison.Ordinal));
        var rows = doc.GetProperty("rows").EnumerateArray().ToList();
        Assert.All(rows, row => Assert.Equal(cols.Count, row.GetArrayLength()));
        Assert.Contains(rows, row => row[at].GetString() == Token);
    }

    [Fact]
    public void AnOffOrderDenseScanCarriesTheNoteOnce()
    {
        var doc = Doc(RecordsTools.Records(Svc, types: new[] { "WEAP", "SPEL" }, source: Plugin(W.OldName), format: "dense", project: Fields("Effects")));
        Assert.Single(doc.GetProperty("notes").EnumerateArray(), n => n.GetString()!.StartsWith("note: project.fields 'Effects'", StringComparison.Ordinal));
        int at = doc.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList().IndexOf("Effects");
        Assert.Contains(doc.GetProperty("rows").EnumerateArray(), row => row[at].GetString() == Token);
    }

    [Fact]
    public void TheRowsFormReadsTheTokenOnTheTypeThatLacksTheList()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.SpellA), Fid(W.Weapons[0]) }, project: new() { form = "rows", fields = new[] { "Effects" } });
        Served(r, "Effects = " + Token);
        Assert.Equal(1, CountOf(r, "note: project.fields 'Effects'"));
    }

    [Fact]
    public void TheOverlayPoleReadsTheTokenToo()
    {
        var r = RecordsTools.Records(Svc, formids: WeaponAndArmor, source: Overlay("post"), project: Fields("ArmorRating"));
        Served(r, "ArmorRating = " + Token);
        Assert.Equal(1, CountOf(r, NoteHead));
    }

    [Fact]
    public void ATreeOverMixedTypesIsCompleteAndSaysNotOnWeapon()
    {
        var r = RecordsTools.Records(Svc, formids: WeaponAndArmor, project: new() { form = "tree", fields = new[] { "ArmorRating" } });
        Served(r, "ArmorRating (not on Weapon)");
        Assert.DoesNotContain("INCOMPLETE", r);
        Assert.DoesNotContain("NO FIELD", r);
    }

    [Fact]
    public void ADeltaOverMixedTypesCountsNoPathAsNamingNoField()
    {
        var r = RecordsTools.Records(Svc, formids: WeaponAndArmor, source: Plugin(W.MasterName), versus: Plugin("winner"),
                                     project: new() { form = "delta", fields = new[] { "ArmorRating" } });
        Served(r, "ArmorRating (not on Weapon)");
        Assert.DoesNotContain("names no field", r);
        Assert.DoesNotContain("INCOMPLETE", r);
    }

    [Fact]
    public void ADeltaOverMixedTypesSaysWhatItDidNotCompareInJsonByCount()
    {
        var doc = Doc(RecordsTools.Records(Svc, formids: WeaponAndArmor, source: Plugin(W.MasterName), versus: Plugin("winner"), format: "json",
                                           project: new() { form = "delta", fields = new[] { "ArmorRating" } }));
        Assert.All(doc.GetProperty("rows").EnumerateArray(), row => Assert.False(row.TryGetProperty("no_field_count", out _)));
    }

    [Fact]
    public void APathNoTypeInScopeCarriesIsRefusedOnceAsWhereRefusesIt()
    {
        var fields = RecordsTools.Records(Svc, types: new[] { "WEAP" }, project: Fields("ArmorRating"));
        var where = RecordsTools.Records(Svc, types: new[] { "WEAP" }, where: new[] { "ArmorRating exists" });
        Refused(fields, "project.fields 'ArmorRating'", "'ArmorRating' is not a field of Weapon");
        Assert.Contains("error: predicate 'ArmorRating exists': 'ArmorRating' is not a field of Weapon", where);
        Assert.Equal(1, CountOf(fields, "is not a field"));
    }

    [Fact]
    public void AWrongPathOnAComparisonIsRefusedBeforeTheRead() =>
        Refused(RecordsTools.Records(Svc, formids: WeaponAndArmor, project: new() { form = "tree", fields = new[] { "ArmorRatnig" } }),
                "did you mean 'ArmorRating'?");

    [Fact]
    public void AnUntypedListKeepsTheCaseSlipHintInItsNote()
    {
        var r = RecordsTools.Records(Svc, formids: new[] { Fid(W.Weapons[0]) }, project: Fields("DATA"));
        Served(r, "DATA = " + Token, "did you mean 'Data'?");
    }

    /// <summary>Unchanged: a single-type read of a field that exists, and a per-record absent value, read as before.</summary>
    [Fact]
    public void ASingleTypeReadOfARealFieldIsUntouched()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "WEAP" }, project: Fields("BasicStats.Damage", "Name"));
        Served(r, "BasicStats.Damage = ");
        Assert.DoesNotContain("note: project.fields", r);
        Assert.DoesNotContain("(not on", r);
    }

    [Fact]
    public void AnAbsentValueStaysPerRow()
    {
        var r = RecordsTools.Records(Svc, formids: WeaponAndArmor, project: Fields("ObjectEffect", "ArmorRating"));
        Served(r);
        Assert.DoesNotContain("note: project.fields 'ObjectEffect'", r);
        Assert.DoesNotContain("ObjectEffect = (not on", r);
    }
}
