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

    /// <summary>A walk's types= picks its seeds, not the types it reaches, so a path only a reached type carries is read, not refused.</summary>
    [Fact]
    public void AWalksRowsPlanOverWhatItReachesNotItsSeedTypes()
    {
        var r = RecordsTools.Records(Svc, types: new[] { "SPEL" }, walk: new RecordsTools.RecordsWalk(), project: Fields("MagicSkill"));
        Served(r, "MagicSkill = (not on Spell)");
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

    static RecordsTools.RecordsProject Compare(string form, params string[] fields) => new() { form = form, fields = fields };

    string Delta(string? format = null, bool countsOnly = false, string? toFile = null, params string[] fields) =>
        RecordsTools.Records(Svc, formids: WeaponAndArmor, source: Plugin(W.MasterName), versus: Plugin("winner"), format: format,
                             counts_only: countsOnly, to_file: toFile, project: Compare("delta", fields));

    static JsonElement RowOf(JsonElement doc, string type) =>
        doc.GetProperty("rows").EnumerateArray().Single(x => x.GetProperty("type").GetString() == type);

    /// <summary>A node whose every path its type lacks compared nothing, so it is neither identical nor a wrong path.</summary>
    [Fact]
    public void ATreeNodeThatComparedNothingSaysSoNotIdentical()
    {
        var r = RecordsTools.Records(Svc, formids: WeaponAndArmor, project: Compare("tree", "ArmorRating"));
        Served(r, "nothing compared, not 'identical': ArmorRating (not on Weapon)");
        Assert.DoesNotContain("identical to", r);
        Assert.DoesNotContain("INCOMPLETE", r);
        Assert.DoesNotContain("NO FIELD", r);
    }

    [Fact]
    public void AJsonTreeNodeThatComparedNothingIsNotComplete()
    {
        var doc = Doc(RecordsTools.Records(Svc, formids: WeaponAndArmor, format: "json", project: Compare("tree", "ArmorRating")));
        var nodes = RowOf(doc, "Weapon").GetProperty("nodes").EnumerateArray().Where(n => n.TryGetProperty("complete", out _)).ToList();
        Assert.NotEmpty(nodes);
        Assert.All(nodes, n =>
        {
            Assert.False(n.GetProperty("complete").GetBoolean());
            Assert.True(n.GetProperty("nothing_compared").GetBoolean());
            Assert.Equal("ArmorRating (not on Weapon)", n.GetProperty("not_on_type").EnumerateArray().Single().GetString());
        });
    }

    [Fact]
    public void ADeltaRowThatComparedNothingIsCountedApartFromIdentical()
    {
        var r = Delta(fields: "ArmorRating");
        Served(r, "2 record(s): 0 differing, 1 identical, 1 with no path read on its type (nothing compared), 0 error(s)",
               "nothing compared, NOT 'identical': ArmorRating (not on Weapon).");
        Assert.Equal(1, CountOf(r, "identical across the fields read"));
        Assert.DoesNotContain("names no field", r);
        Assert.DoesNotContain("INCOMPLETE", r);
        Served(Delta(countsOnly: true, fields: "ArmorRating"), "count=2 differing=0 identical=1 no_verdict=0 errors=0 not_compared=1");
    }

    [Fact]
    public void AJsonDeltaRowThatComparedNothingIsNotCompleteAndNamesThePath()
    {
        foreach (var doc in new[] { Doc(Delta("json", fields: "ArmorRating")), Doc(Delta("json", countsOnly: true, fields: "ArmorRating")) })
        {
            Assert.Equal(1, doc.GetProperty("identical").GetInt32());
            Assert.Equal(1, doc.GetProperty("not_compared").GetInt32());
        }
        var json = Doc(Delta("json", fields: "ArmorRating"));
        var weapon = RowOf(json, "Weapon");
        Assert.False(weapon.GetProperty("complete").GetBoolean());
        Assert.True(weapon.GetProperty("nothing_compared").GetBoolean());
        Assert.Equal("ArmorRating (not on Weapon)", weapon.GetProperty("not_on_type").EnumerateArray().Single().GetString());
        Assert.False(weapon.TryGetProperty("no_field_count", out _));
        var armor = RowOf(json, "Armor");
        Assert.True(armor.GetProperty("complete").GetBoolean());
        Assert.False(armor.TryGetProperty("not_on_type", out _));
    }

    /// <summary>A row that compared one path and lacks another is complete and names the lacking one, in json as in text.</summary>
    [Fact]
    public void AJsonDeltaRowThatComparedSomethingNamesWhatItLackedAndStaysComplete()
    {
        var doc = Doc(Delta("json", fields: new[] { "ArmorRating", "BasicStats.Damage" }));
        Assert.False(doc.TryGetProperty("not_compared", out _));
        foreach (var (type, notOn) in new[] { ("Weapon", "ArmorRating (not on Weapon)"), ("Armor", "BasicStats.Damage (not on Armor)") })
        {
            var row = RowOf(doc, type);
            Assert.True(row.GetProperty("complete").GetBoolean());
            Assert.False(row.TryGetProperty("nothing_compared", out _));
            Assert.Equal(notOn, row.GetProperty("not_on_type").EnumerateArray().Single().GetString());
        }
        var text = Delta(fields: new[] { "ArmorRating", "BasicStats.Damage" });
        Served(text, "2 record(s): 1 differing, 1 identical, 0 error(s)",
               "identical across the fields read (1 value leaf/leaves agree, BasicStats.Damage (not on Armor) not compared).");
        Assert.DoesNotContain("nothing compared", text);
    }

    [Fact]
    public void TheDeltaArtifactNamesTheRowThatComparedNothing()
    {
        var art = W.Scratch("delta-not-on.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(art)!);
        Delta(toFile: art, fields: "ArmorRating");
        var lines = File.ReadLines(art).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();
        var schema = lines[0].GetProperty("row_schema").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("not_on_type?", schema);
        Assert.Contains("nothing_compared?", schema);
        var weapon = lines.Single(l => l.TryGetProperty("type", out var t) && t.GetString() == "Weapon");
        Assert.True(weapon.GetProperty("nothing_compared").GetBoolean());
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
